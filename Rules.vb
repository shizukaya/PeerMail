Option Strict On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text

''' <summary>
''' 受信メッセージの振り分けルール。指定した条件を「すべて」満たしたとき、メッセージを移動先のフォルダへ移す。
''' 条件は、1つ以上指定する必要がある(指定しない項目は無視する)。文字の比較は、大文字・小文字を区別しない。
''' </summary>
Public Class MailRule
    Public Property Enabled As Boolean = True
    Public Property Name As String = ""
    ''' <summary>0=指定なし / 1=連絡先に登録済みの人 / 2=連絡先に未登録の人</summary>
    Public Property SenderKind As Integer
    ''' <summary>特定の連絡先(実効ID)。空なら指定なし</summary>
    Public Property SenderId As String = ""
    ''' <summary>差出人の表示名に含まれる文字。登録済みの人は連絡先の表示名、未登録の人は相手が名乗っている表示名と比べる</summary>
    Public Property SenderNameContains As String = ""
    ''' <summary>本文に含まれる文字</summary>
    Public Property BodyContains As String = ""
    ''' <summary>移動先: ユーザーフォルダのID、または ごみ箱(FolderStore.TrashId)</summary>
    Public Property TargetFolderId As String = ""

    Public Function HasCondition() As Boolean
        Return SenderKind <> 0 OrElse SenderId.Length > 0 OrElse SenderNameContains.Length > 0 OrElse BodyContains.Length > 0
    End Function

    ''' <summary>指定したすべての条件を満たすか</summary>
    ' 注意: VBは大文字・小文字を区別しないため、引数名をプロパティ名(SenderId など)と重ねてはいけない
    Public Function Matches(fromId As String, fromName As String, isRegistered As Boolean, body As String) As Boolean
        If Not HasCondition() Then Return False
        If SenderKind = 1 AndAlso Not isRegistered Then Return False
        If SenderKind = 2 AndAlso isRegistered Then Return False
        If SenderId.Length > 0 AndAlso SenderId <> fromId Then Return False
        If SenderNameContains.Length > 0 AndAlso fromName.IndexOf(SenderNameContains, StringComparison.OrdinalIgnoreCase) < 0 Then Return False
        If BodyContains.Length > 0 AndAlso body.IndexOf(BodyContains, StringComparison.OrdinalIgnoreCase) < 0 Then Return False
        Return True
    End Function

    Public Function Copy() As MailRule
        Return DirectCast(MemberwiseClone(), MailRule)
    End Function
End Class

Public Module RuleEngine

    ''' <summary>
    ''' 上から順に調べて、最初に条件を満たした(有効で、移動先が使える)ルールを返す。なければ Nothing。
    ''' isValidTarget: 移動先のフォルダが今も存在するか
    ''' </summary>
    Public Function FindRule(rules As IEnumerable(Of MailRule), senderId As String, senderName As String, isRegistered As Boolean,
                             body As String, isValidTarget As Func(Of String, Boolean)) As MailRule
        For Each r In rules
            If r.Enabled AndAlso r.TargetFolderId.Length > 0 AndAlso isValidTarget(r.TargetFolderId) AndAlso
               r.Matches(senderId, senderName, isRegistered, body) Then
                Return r
            End If
        Next
        Return Nothing
    End Function

End Module

''' <summary>
''' Rules.dat : 1行 = 有効(1/0) [TAB] ルール名 [TAB] 差出人の種類(0/1/2) [TAB] 差出人の実効ID [TAB] 差出人名に含む文字
'''   [TAB] 本文に含む文字 [TAB] 移動先フォルダID。ルールの順番 = ファイルの行の順。
''' </summary>
Public Module RuleStore

    Public Function Load(path As String) As List(Of MailRule)
        Dim res As New List(Of MailRule)
        If Not File.Exists(path) Then Return res
        For Each line In File.ReadAllLines(path, Encoding.UTF8)
            Dim p = line.Split(ControlChars.Tab)
            Dim kind As Integer
            If p.Length = 7 AndAlso Integer.TryParse(p(2), kind) AndAlso kind >= 0 AndAlso kind <= 2 Then
                res.Add(New MailRule() With {
                    .Enabled = (p(0) = "1"), .Name = p(1), .SenderKind = kind, .SenderId = p(3),
                    .SenderNameContains = p(4), .BodyContains = p(5), .TargetFolderId = p(6)})
            End If
        Next
        Return res
    End Function

    Public Sub Save(path As String, rules As IEnumerable(Of MailRule))
        WriteLinesSafe(path, rules.Select(Function(r) String.Join(vbTab,
            If(r.Enabled, "1", "0"), r.Name, r.SenderKind.ToString(), r.SenderId, r.SenderNameContains, r.BodyContains, r.TargetFolderId)))
    End Sub

End Module
