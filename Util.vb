Option Strict On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Numerics
Imports System.Security.Cryptography
Imports System.Text

''' <summary>Base58・長さ付き連結・フレーム入出力などの共通処理</summary>
Public Module Util

    Private Const Alphabet As String = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"

    ''' <summary>1フレームの最大サイズ(1MB)。巨大データによるメモリ枯渇を防ぐ</summary>
    Public Const MaxFrame As Integer = 1024 * 1024

    ' ---------- Base58 / 実効ID ----------

    Public Function Base58Encode(data As Byte()) As String
        Dim zeros = 0
        While zeros < data.Length AndAlso data(zeros) = 0
            zeros += 1
        End While

        Dim n As New BigInteger(data, isUnsigned:=True, isBigEndian:=True)
        Dim sb As New StringBuilder()
        Dim rem58 As BigInteger
        While n > BigInteger.Zero
            n = BigInteger.DivRem(n, 58, rem58)
            sb.Insert(0, Alphabet(CInt(rem58)))
        End While
        sb.Insert(0, "1"c, zeros)
        Return sb.ToString()
    End Function

    ''' <summary>実効ID = Base58(SHA-256(公開鍵 SubjectPublicKeyInfo))</summary>
    Public Function IdFromPublicKey(spki As Byte()) As String
        Return Base58Encode(SHA256.HashData(spki))
    End Function

    ''' <summary>文字列の先頭から count 行(改行コードLF区切り)を取り除いた文字列を返す</summary>
    Public Function DropFirstLines(text As String, count As Integer) As String
        Dim pos = 0
        For i = 1 To count
            Dim nl = text.IndexOf(vbLf, pos, StringComparison.Ordinal)
            If nl < 0 Then Return ""
            pos = nl + 1
        Next
        Return text.Substring(pos)
    End Function

    ''' <summary>表示名として使える形に整える(制御文字と @ を除き、前後の空白を除き、40文字まで)</summary>
    Public Function CleanDisplayName(s As String) As String
        If s Is Nothing Then Return ""
        Dim t = New String(s.Where(Function(ch) ch <> "@"c AndAlso Not Char.IsControl(ch)).ToArray()).Trim()
        Return If(t.Length > 40, t.Substring(0, 40).Trim(), t)
    End Function

    Public Function IsValidId(id As String) As Boolean
        Return id.Length >= 40 AndAlso id.Length <= 46 AndAlso id.All(Function(c) Alphabet.IndexOf(c) >= 0)
    End Function

    ''' <summary>"host:port" を分解する</summary>
    Public Function ParseEndpoint(s As String) As (Host As String, Port As Integer)
        s = s.Trim()
        Dim i = s.LastIndexOf(":"c)
        Dim p As Integer
        If i <= 0 OrElse Not Integer.TryParse(s.Substring(i + 1), p) OrElse p < 1 OrElse p > 65535 Then
            Throw New FormatException("アドレスは「IPアドレス:ポート」の形式で入力してください")
        End If
        Return (s.Substring(0, i).Trim(), p)
    End Function

    ' ---------- 長さ付き連結 (通信データと署名対象の両方に使う) ----------

    ''' <summary>各パートに4バイト長を付けて連結する。区切りが曖昧にならないので署名対象にも安全</summary>
    Public Function Pack(ParamArray parts As Byte()()) As Byte()
        Using ms As New MemoryStream()
            For Each p In parts
                ms.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(p.Length)), 0, 4)
                ms.Write(p, 0, p.Length)
            Next
            Return ms.ToArray()
        End Using
    End Function

    Public Function Unpack(data As Byte()) As List(Of Byte())
        Dim res As New List(Of Byte())
        Dim pos = 0
        While pos < data.Length
            If pos + 4 > data.Length Then Throw New InvalidDataException("データ形式が不正です")
            Dim len = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(data, pos))
            pos += 4
            If len < 0 OrElse len > data.Length - pos Then Throw New InvalidDataException("データ形式が不正です")
            Dim part(len - 1) As Byte
            Buffer.BlockCopy(data, pos, part, 0, len)
            res.Add(part)
            pos += len
        End While
        Return res
    End Function

    ' ---------- フレーム入出力 (4バイト長 + 本体) ----------

    Public Sub WriteFrame(s As Stream, data As Byte())
        s.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(data.Length)), 0, 4)
        s.Write(data, 0, data.Length)
        s.Flush()
    End Sub

    Public Function ReadFrame(s As Stream) As Byte()
        Dim head = ReadExact(s, 4)
        Dim len = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(head, 0))
        If len < 0 OrElse len > MaxFrame Then Throw New InvalidDataException("フレームサイズが不正です")
        Return ReadExact(s, len)
    End Function

    Private Function ReadExact(s As Stream, n As Integer) As Byte()
        Dim buf(n - 1) As Byte
        Dim off = 0
        While off < n
            Dim r = s.Read(buf, off, n - off)
            If r <= 0 Then Throw New EndOfStreamException("接続が途中で閉じられました")
            off += r
        End While
        Return buf
    End Function

End Module
