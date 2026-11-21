Option Strict On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text

''' <summary>Contacts.dat の1行 = 表示名 / 実効ID / IP:ポート (IP:ポートが空ならホストで検索)</summary>
Public Class Contact
    Public Property Name As String = ""
    Public Property Id As String = ""
    Public Property Endpoint As String = ""
    ' 予約メッセージの問い合わせ(ツールバーの「予約を確認」と、自動の問い合わせ)の対象にするか
    Public Property Poll As Boolean = True

    Public Overrides Function ToString() As String
        Dim dest = If(Endpoint.Length > 0, Endpoint, "ホストで検索")
        Dim note = If(Poll, "", "  (予約を問い合わせない)")
        Return $"{Name}@{Id}  [{dest}]{note}"      ' 連絡先一覧・宛先リストには、実効IDを省略せず全体を表示する
    End Function
End Class

''' <summary>
''' Contacts.dat : 表示名 [TAB] 実効ID [TAB] IP:ポート [TAB] 予約メッセージの問い合わせ(省略可。0=問い合わせない。省略時は問い合わせる)
''' 問い合わせる相手(既定)は4列目を書かないので、4列目に対応していない古いバージョンでも読める。
''' </summary>
Public Module ContactStore

    Public Function Load(path As String) As List(Of Contact)
        Dim res As New List(Of Contact)
        If Not File.Exists(path) Then Return res
        For Each line In File.ReadAllLines(path, Encoding.UTF8)
            Dim p = line.Split(ControlChars.Tab)
            If p.Length = 3 OrElse p.Length = 4 Then
                res.Add(New Contact() With {.Name = p(0), .Id = p(1), .Endpoint = p(2), .Poll = (p.Length = 3 OrElse p(3) <> "0")})
            End If
        Next
        Return res
    End Function

    Public Sub Save(path As String, contacts As IEnumerable(Of Contact))
        WriteLinesSafe(path, contacts.Select(Function(c) c.Name & vbTab & c.Id & vbTab & c.Endpoint & If(c.Poll, "", vbTab & "0")))
    End Sub

End Module

''' <summary>受信または送信した1通。PeerId は 受信なら差出人、送信なら宛先の実効ID</summary>
Public Class MailItem
    Public Property Time As DateTime
    Public Property PeerId As String = ""
    Public Property Text As String = ""
    ''' <summary>受信時の署名の証拠(受信したメッセージのみ。記録のないものは Nothing)</summary>
    Public Property Evidence As Evidence
    ''' <summary>受信メッセージで、差出人が名乗っていた表示名(署名の証拠から取り出す。記録がなければ空)</summary>
    Public ReadOnly Property ClaimedName As String
        Get
            Return If(Evidence IsNot Nothing, Evidence.ClaimedName, "")
        End Get
    End Property
    ''' <summary>送信したメッセージなら True (Sent.dat から読み込んだもの)</summary>
    Public Property IsSent As Boolean
    ''' <summary>送信予約(まだ相手に渡していないメッセージ)なら True。保存はされない(Outbox.dat から読み込んだものに付ける)</summary>
    Public Property IsReserved As Boolean
    ''' <summary>所属するフォルダのID。空なら標準の場所(受信は受信トレイ、送信は送信済み)、"@trash" ならごみ箱</summary>
    Public Property Folder As String = ""
End Class

Public Enum FolderKind
    Inbox
    Sent
    Outbox
    User
    Trash
    Log
End Enum

''' <summary>フォルダ一覧の1項目。受信トレイ・送信済み・ログは固定で、User はユーザーが作成したフォルダ</summary>
Public Class FolderItem
    Public Property Kind As FolderKind
    Public Property Id As String = ""
    Public Property Name As String = ""

    Public Overrides Function ToString() As String
        Return Name
    End Function
End Class

Public Module FileUtil
    ''' <summary>一時ファイルに書いてから置き換える(書き込み途中で失敗しても元のファイルを壊さない)</summary>
    Public Sub WriteLinesSafe(path As String, lines As IEnumerable(Of String))
        Dim tmp = path & ".tmp"
        File.WriteAllLines(tmp, lines, Encoding.UTF8)
        If File.Exists(path) Then
            File.Replace(tmp, path, Nothing)
        Else
            File.Move(tmp, path)
        End If
    End Sub
End Module

''' <summary>Folders.dat : ユーザーが作成したフォルダ。1行 = フォルダID [TAB] フォルダ名</summary>
Public Module FolderStore

    ''' <summary>ごみ箱を表す所属フォルダID。メッセージの Folder がこの値なら、ごみ箱に入っている</summary>
    Public Const TrashId As String = "@trash"

    Public Function NewId() As String
        Return Guid.NewGuid().ToString("N")
    End Function

    Public Function Load(path As String) As List(Of FolderItem)
        Dim res As New List(Of FolderItem)
        If Not File.Exists(path) Then Return res
        For Each line In File.ReadAllLines(path, Encoding.UTF8)
            Dim p = line.Split(ControlChars.Tab)
            If p.Length = 2 AndAlso p(0).Length > 0 AndAlso p(1).Length > 0 Then
                res.Add(New FolderItem() With {.Kind = FolderKind.User, .Id = p(0), .Name = p(1)})
            End If
        Next
        Return res
    End Function

    Public Sub Save(path As String, folders As IEnumerable(Of FolderItem))
        WriteLinesSafe(path, folders.Select(Function(f) f.Id & vbTab & f.Name))
    End Sub

End Module

''' <summary>
''' メッセージの保存。1行 = 日時 [TAB] 相手の実効ID [TAB] 本文(改行・タブ・バックスラッシュはエスケープ)
'''   [TAB] 署名の証拠(受信のみ・省略可) [TAB] 所属フォルダID(省略可)
''' 受信は Messages.dat、送信は Sent.dat に保存する。
''' </summary>
Public Module MailStore

    Private Const TimeFormat As String = "yyyy-MM-dd HH:mm:ss"

    Public Function Load(path As String, isSent As Boolean) As List(Of MailItem)
        Dim res As New List(Of MailItem)
        If Not File.Exists(path) Then Return res
        For Each line In File.ReadAllLines(path, Encoding.UTF8)
            Dim p = line.Split(ControlChars.Tab)
            Dim t As DateTime
            If p.Length >= 3 AndAlso p.Length <= 5 AndAlso DateTime.TryParseExact(p(0), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, t) Then
                Dim item As New MailItem() With {.Time = t, .PeerId = p(1), .Text = Unesc(p(2)), .IsSent = isSent}
                If p.Length >= 4 AndAlso p(3).Length > 0 Then item.Evidence = Evidence.TryParse(p(3))
                If p.Length = 5 Then item.Folder = p(4)
                res.Add(item)
            End If
        Next
        Return res
    End Function

    ''' <summary>1通を末尾に追記する(新しいメッセージの保存用)</summary>
    Public Sub Append(path As String, item As MailItem)
        File.AppendAllText(path, FormatLine(item) & Environment.NewLine, Encoding.UTF8)
    End Sub

    ''' <summary>全件を書き直す(削除・移動の反映用)。一時ファイル経由なので途中で失敗しても元のファイルは残る</summary>
    Public Sub Save(path As String, items As IEnumerable(Of MailItem))
        WriteLinesSafe(path, items.Select(Function(m) FormatLine(m)))
    End Sub

    Private Function FormatLine(item As MailItem) As String
        Dim parts As New List(Of String) From {
            item.Time.ToString(TimeFormat, CultureInfo.InvariantCulture), item.PeerId, Esc(item.Text)}
        If item.Evidence IsNot Nothing OrElse item.Folder.Length > 0 Then
            parts.Add(If(item.Evidence IsNot Nothing, item.Evidence.ToBase64(), ""))
            If item.Folder.Length > 0 Then parts.Add(item.Folder)
        End If
        Return String.Join(vbTab, parts)
    End Function

    Private Function Esc(s As String) As String
        Return s.Replace("\", "\\").Replace(vbCr, "\r").Replace(vbLf, "\n").Replace(vbTab, "\t")
    End Function

    Private Function Unesc(s As String) As String
        Dim sb As New StringBuilder()
        Dim i = 0
        While i < s.Length
            If s(i) = "\"c AndAlso i + 1 < s.Length Then
                i += 1
                Select Case s(i)
                    Case "n"c
                        sb.Append(vbLf)
                    Case "r"c
                        sb.Append(vbCr)
                    Case "t"c
                        sb.Append(vbTab)
                    Case Else
                        sb.Append(s(i))
                End Select
            Else
                sb.Append(s(i))
            End If
            i += 1
        End While
        Return sb.ToString()
    End Function

End Module
