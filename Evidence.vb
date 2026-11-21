Option Strict On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text

''' <summary>1項目の検証結果</summary>
Public Class VerifyStep
    Public Property Ok As Boolean
    Public Property Text As String = ""
End Class

''' <summary>署名の再検証の結果</summary>
Public Class VerifyReport
    Public ReadOnly Steps As New List(Of VerifyStep)
    Public ReadOnly Notes As New List(Of String)

    Public Sub Add(ok As Boolean, text As String)
        Steps.Add(New VerifyStep() With {.Ok = ok, .Text = text})
    End Sub

    Public ReadOnly Property AllOk As Boolean
        Get
            Return Steps.Count > 0 AndAlso Steps.All(Function(s) s.Ok)
        End Get
    End Property

    Public Function ToText() As String
        Dim sb As New StringBuilder()
        For Each s In Steps
            sb.AppendLine((If(s.Ok, "[OK] ", "[NG] ")) & s.Text)
        Next
        If Notes.Count > 0 Then
            sb.AppendLine()
            For Each n In Notes
                sb.AppendLine(n)
            Next
        End If
        Return sb.ToString()
    End Function
End Class

''' <summary>
''' 受信メッセージ1通分の「署名の証拠」。受信時にパケットの各項目・差出人の署名・差出人の公開鍵・
''' そのメッセージの復号に使った鍵を保存しておき、後から署名と本文を再検証できるようにする。
''' Messages.dat には Base64 の1列として本文と一緒に保存する。
''' </summary>
Public Class Evidence

    ' パケットの項目 + 差出人の公開鍵 + 復号に使った鍵 の順に保存する。
    '   バージョン1(表示名なし) : 0 Version / 1 FromID / 2 ToID / 3 Timestamp / 4 SequenceNo / 5 Nonce / 6 暗号文 / 7 認証タグ / 8 署名 / 9 公開鍵 / 10 復号鍵 (計11項目)
    '   バージョン2(表示名あり) : 上の 0〜8 に続けて 9 差出人が名乗る表示名 / 10 公開鍵 / 11 復号鍵 (計12項目)
    Private Const PartCountV1 As Integer = 11
    Private Const PartCountV2 As Integer = 12

    Private ReadOnly _parts As List(Of Byte())

    Private Sub New(parts As List(Of Byte()))
        _parts = parts
    End Sub

    ''' <param name="packet">受信パケットの9項目 (Version, FromID, ToID, Timestamp, SequenceNo, Nonce, 暗号文, タグ, 署名)</param>
    Public Shared Function Create(packet As List(Of Byte()), senderPublicKey As Byte(), sessionKey As Byte()) As Evidence
        Dim parts As New List(Of Byte())(packet)
        parts.Add(senderPublicKey)
        parts.Add(sessionKey)
        Return New Evidence(parts)
    End Function

    ''' <summary>バージョン2のパケットか(差出人が表示名を名乗っているか)</summary>
    Private ReadOnly Property HasName As Boolean
        Get
            Return _parts.Count = PartCountV2
        End Get
    End Property

    ''' <summary>差出人が名乗っていた表示名(署名の対象なので、受信後に書き換えれば検証で見つかる)。名乗りのない旧形式は空</summary>
    Public ReadOnly Property ClaimedName As String
        Get
            If Not HasName Then Return ""
            Return CleanDisplayName(Encoding.UTF8.GetString(_parts(9)))
        End Get
    End Property

    Public Function ToBase64() As String
        Return Convert.ToBase64String(Pack(_parts.ToArray()))
    End Function

    ''' <summary>保存された文字列から復元する。形式が不正なら Nothing</summary>
    Public Shared Function TryParse(b64 As String) As Evidence
        Try
            Dim parts = Unpack(Convert.FromBase64String(b64))
            If parts.Count <> PartCountV1 AndAlso parts.Count <> PartCountV2 Then Return Nothing
            Return New Evidence(parts)
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' 署名と本文を再検証する。
    ''' peerId = 保存されている差出人の実効ID / selfId = 自分の実効ID / text = 保存されている本文
    ''' </summary>
    Public Function Verify(peerId As String, selfId As String, text As String) As VerifyReport
        Dim r As New VerifyReport()
        Dim p = _parts
        Dim pub = p(p.Count - 2)
        Dim key = p(p.Count - 1)

        ' 1) 公開鍵 → 実効ID が、保存されている差出人IDと一致するか
        Try
            r.Add(IdFromPublicKey(pub) = peerId, "記録された公開鍵のハッシュが、差出人の実効IDと一致する")
        Catch ex As Exception
            r.Add(False, "公開鍵を読み取れない: " & ex.Message)
        End Try

        ' 2) パケット内の差出人ID・宛先ID
        r.Add(Encoding.UTF8.GetString(p(1)) = peerId, "パケットに書かれた差出人IDが、差出人の実効IDと一致する")
        If selfId.Length > 0 Then
            r.Add(Encoding.UTF8.GetString(p(2)) = selfId, "パケットに書かれた宛先IDが、自分の実効IDと一致する")
        End If

        ' 3) 差出人の電子署名 (Version, FromID, ToID, Timestamp, SequenceNo, Nonce, 暗号文, タグ)
        Try
            Using dsa = Messenger.ImportPublic(pub)
                ' バージョン2は、差出人が名乗る表示名(p(9))も署名の対象
                Dim signedData = If(HasName,
                                    Pack(p(0), p(1), p(2), p(3), p(4), p(5), p(6), p(7), p(9)),
                                    Pack(p(0), p(1), p(2), p(3), p(4), p(5), p(6), p(7)))
                Dim ok = dsa.VerifyData(signedData, p(8), HashAlgorithmName.SHA256)
                r.Add(ok, If(HasName, "差出人の電子署名が、記録された暗号文と表示名に対して正しい", "差出人の電子署名が、記録された暗号文などに対して正しい"))
            End Using
        Catch ex As Exception
            r.Add(False, "署名を検証できない: " & ex.Message)
        End Try

        ' 4) 暗号文を記録した鍵で復号して、保存されている本文と比べる (AES-GCMのタグも同時に確認される)
        Try
            Dim plain(p(6).Length - 1) As Byte
            Using aes As New AesGcm(key, 16)
                aes.Decrypt(p(5), p(6), p(7), plain, Pack(p(0), p(1), p(2), p(3), p(4)))
            End Using
            r.Add(Encoding.UTF8.GetString(plain) = text, "記録された暗号文を鍵で復号した結果が、保存されている本文と一致する")
        Catch ex As Exception
            r.Add(False, "復号できない(暗号文・タグ・鍵のいずれかが変更されている可能性): " & ex.Message)
        End Try

        ' 参考情報 (どちらも差出人が申告した値)
        Try
            r.Notes.Add("差出人が申告した送信時刻(UTC): " & Encoding.UTF8.GetString(p(3)))
            r.Notes.Add(If(HasName, "差出人が名乗っていた表示名: " & ClaimedName, "差出人が名乗っていた表示名: (なし。表示名を伝える前の形式)"))
            r.Notes.Add("SequenceNo: " & BitConverter.ToInt64(p(4), 0).ToString())
        Catch
        End Try
        Return r
    End Function

End Class
