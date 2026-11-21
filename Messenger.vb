Option Strict On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks

''' <summary>
''' 自分の鍵ペア。User.dat に 表示名 / 秘密鍵(PKCS#8) / 公開鍵 を保存する。
''' 注意: 秘密鍵は暗号化せずに保存している(外部ライブラリ不使用のため)。
''' </summary>
Public Class Identity

    Public Property DisplayName As String = ""
    Public ReadOnly Dsa As ECDsa
    Public ReadOnly PublicKey As Byte()
    Public ReadOnly Id As String

    Private Sub New(dsa As ECDsa, name As String)
        Me.Dsa = dsa
        PublicKey = dsa.ExportSubjectPublicKeyInfo()
        Id = IdFromPublicKey(PublicKey)
        DisplayName = name
    End Sub

    Public ReadOnly Property PublicId As String
        Get
            Return DisplayName & "@" & Id
        End Get
    End Property

    Public Shared Function LoadOrCreate(path As String) As Identity
        If File.Exists(path) Then
            Dim lines = File.ReadAllLines(path, Encoding.UTF8)
            If lines.Length >= 2 Then
                Dim dsa = ECDsa.Create()
                Dim read As Integer
                dsa.ImportPkcs8PrivateKey(Convert.FromBase64String(lines(1)), read)
                Return New Identity(dsa, lines(0))
            End If
        End If

        Dim ident As New Identity(ECDsa.Create(ECCurve.NamedCurves.nistP256), Environment.UserName)
        ident.Save(path)
        Return ident
    End Function

    Public Sub Save(path As String)
        File.WriteAllLines(path,
            {DisplayName, Convert.ToBase64String(Dsa.ExportPkcs8PrivateKey()), Convert.ToBase64String(PublicKey)},
            Encoding.UTF8)
    End Sub

End Class

''' <summary>
''' 送受信本体。1接続 = 1回の「公開鍵ハッシュ確認 → 相互認証(署名) → 一時ECDH → AES-256-GCM」のあとで、次のいずれかを行う。
'''   (1) メッセージ1通を送る(従来)
'''   (2) 予約の問い合わせ: 接続した側が「自分宛のメッセージはありますか」と尋ね、受けた側が予約を渡す。
'''       受け取る側は外向きの接続だけでよいので、ポート開放なしで受信できる(渡す側は受信待機中である必要がある)。
''' </summary>
Public Class Messenger
    Implements IDisposable

    Public Event MessageReceived(fromId As String, text As String, evidence As Evidence)
    Public Event Info(message As String)
    ''' <summary>予約メッセージを相手に渡せたとき(予約からは外し済み)</summary>
    Public Event ReservedDelivered(item As MailItem)

    Private Const ProtocolVersion As Byte = 2     ' 2: 差出人が名乗る表示名を付けて(署名の対象にして)送る
    Private Const LegacyVersion As Byte = 1       ' 1: 表示名なし(古いバージョンの送信側。受信のみ対応)
    Private Const OpPull As Byte = &H50           ' 予約の問い合わせ(接続した側 → 受けた側)
    Private Const OpPullReply As Byte = &H51      ' 予約の問い合わせへの応答(件数)
    Private Const TimeFormat As String = "yyyy-MM-dd'T'HH:mm:ss'Z'"
    Private Const MaxSkewSeconds As Double = 300      ' Timestamp の許容ずれ(5分)
    Private Const MaxText As Integer = 64 * 1024
    Private Const MaxActive As Integer = 16           ' 同時受信数の上限
    Private Const IoTimeout As Integer = 10000
    Private Const ConnectTimeout As Integer = 8000

    Private Shared ReadOnly LabelA As Byte() = Encoding.ASCII.GetBytes("P2PMSG1-INITIATOR")
    Private Shared ReadOnly LabelB As Byte() = Encoding.ASCII.GetBytes("P2PMSG1-RESPONDER")

    Public ReadOnly Property Self As Identity

    ''' <summary>相手に渡すための送信予約。Nothing なら、予約の問い合わせには「0件」と答える</summary>
    Public Property Reservations As ReservationStore

    ''' <summary>予約の問い合わせ1回で受け渡す最大件数</summary>
    Public Property PullBatch As Integer = 50

    Private ReadOnly _sessionPath As String
    Private ReadOnly _lastSeq As New Dictionary(Of String, Long)
    Private ReadOnly _lock As New Object()
    Private ReadOnly _serving As New HashSet(Of String)       ' 予約を渡している最中の相手(同じ相手への二重送りを防ぐ)
    Private _lastSent As Long
    Private _active As Integer
    Private _listener As TcpListener
    Private _cts As CancellationTokenSource

    ''' <summary>認証と鍵共有が済んだ接続1本分の情報</summary>
    Private Class Session
        Implements IDisposable
        Public PeerId As String = ""            ' 認証済みの相手の実効ID
        Public PeerPublicKey As Byte()
        Public PeerDsa As ECDsa                 ' 相手の公開鍵(署名の検証用)
        Public Key As Byte()                    ' このセッション限りの AES-256 鍵

        Public Sub Dispose() Implements IDisposable.Dispose
            If PeerDsa IsNot Nothing Then PeerDsa.Dispose()
        End Sub
    End Class

    Public Sub New(self As Identity, sessionPath As String)
        _Self = self
        _sessionPath = sessionPath
        LoadSession()
    End Sub

    ' ====================== 受信待機 ======================

    Public Sub StartListening(port As Integer)
        If _listener IsNot Nothing Then Return
        Dim l As New TcpListener(IPAddress.Any, port)
        l.Start()
        _listener = l
        _cts = New CancellationTokenSource()
        Dim ct = _cts.Token
        Dim t = Task.Run(Function() AcceptLoop(l, ct))
    End Sub

    Public Sub StopListening()
        If _listener Is Nothing Then Return
        _cts.Cancel()
        _listener.Stop()
        _listener = Nothing
    End Sub

    Private Async Function AcceptLoop(l As TcpListener, ct As CancellationToken) As Task
        Try
            While Not ct.IsCancellationRequested
                Dim cl = Await l.AcceptTcpClientAsync(ct).ConfigureAwait(False)
                If Interlocked.Increment(_active) > MaxActive Then
                    Interlocked.Decrement(_active)
                    cl.Close()
                Else
                    Dim c = cl
                    Dim t = Task.Run(Sub() HandleClient(c))
                End If
            End While
        Catch ex As OperationCanceledException
        Catch ex As ObjectDisposedException
        Catch ex As Exception
            RaiseEvent Info("受信待機エラー: " & ex.Message)
        End Try
    End Function

    ''' <summary>接続を受けた側: 認証 → 最初のフレームで「メッセージ」か「予約の問い合わせ」かを判別して処理する</summary>
    Private Sub HandleClient(cl As TcpClient)
        Dim remote As String = "?"
        Try
            If cl.Client.RemoteEndPoint IsNot Nothing Then remote = cl.Client.RemoteEndPoint.ToString()
            cl.ReceiveTimeout = IoTimeout
            cl.SendTimeout = IoTimeout
            Using ns = cl.GetStream()
                Dim fromId As String = Nothing
                Dim text As String = Nothing
                Dim ev As Evidence = Nothing
                Dim received = False
                Try
                    Using s = ResponderHandshake(ns)
                        fromId = s.PeerId
                        Dim first = Unpack(ReadFrame(ns))
                        If IsPullRequest(first) Then
                            ServePull(ns, s, first)
                        Else
                            ProcessPacket(first, s, text, ev)
                            WriteFrame(ns, New Byte() {1})
                            received = True
                        End If
                    End Using
                Catch
                    Try
                        WriteFrame(ns, New Byte() {0})
                    Catch
                    End Try
                    Throw
                End Try
                If received Then RaiseEvent MessageReceived(fromId, text, ev)
            End Using
        Catch ex As Exception
            RaiseEvent Info($"受信を破棄しました ({remote}): {ex.Message}")
        Finally
            cl.Close()
            Interlocked.Decrement(_active)
        End Try
    End Sub

    Private Shared Function IsPullRequest(parts As List(Of Byte())) As Boolean
        Return parts.Count >= 1 AndAlso parts(0).Length = 1 AndAlso parts(0)(0) = OpPull
    End Function

    ''' <summary>
    ''' 予約の問い合わせに答える。認証済みの相手(=問い合わせてきた人の実効ID)宛ての予約だけを渡す。
    ''' 1通ずつ、相手が「受け取った」と返したものだけを予約から外す(届かなかったものは残す)。
    ''' </summary>
    Private Sub ServePull(ns As Stream, s As Session, req As List(Of Byte()))
        Dim limit = Math.Max(1, PullBatch)
        If req.Count >= 2 AndAlso req(1).Length = 4 Then
            limit = Math.Max(1, Math.Min(limit, IPAddress.NetworkToHostOrder(BitConverter.ToInt32(req(1), 0))))
        End If

        Dim items As New List(Of MailItem)
        Dim store = Reservations
        Dim claimed = False
        SyncLock _lock
            claimed = _serving.Add(s.PeerId)
        End SyncLock
        Try
            If claimed AndAlso store IsNot Nothing Then items = store.PeekFor(s.PeerId, limit)
            WriteFrame(ns, Pack(New Byte() {OpPullReply}, BitConverter.GetBytes(IPAddress.HostToNetworkOrder(items.Count))))
            For Each item In items
                WriteFrame(ns, BuildPacket(s.Key, s.PeerId, Encoding.UTF8.GetBytes(item.Text)))
                Dim ack = ReadFrame(ns)
                If ack.Length <> 1 OrElse ack(0) <> 1 Then Exit For      ' 受け取れなかった: 予約は残す
                If store.Remove(item) Then RaiseEvent ReservedDelivered(item)
            Next
        Finally
            If claimed Then
                SyncLock _lock
                    _serving.Remove(s.PeerId)
                End SyncLock
            End If
        End Try
    End Sub

    ' ====================== 認証・鍵共有 ======================

    ''' <summary>接続を受けた側(応答側)のハンドシェイク。接続してきた相手を認証し、セッション鍵を作る</summary>
    Private Function ResponderHandshake(ns As Stream) As Session
        ' 1) 応答側 → 接続側 : 公開鍵 + Nonce
        Dim nonceB = RandomNumberGenerator.GetBytes(32)
        WriteFrame(ns, Pack(Self.PublicKey, nonceB))

        ' 2) 接続側 → 応答側 : 公開鍵 / Nonce / 一時ECDH公開鍵 / 署名
        Dim m1 = Unpack(ReadFrame(ns))
        If m1.Count <> 4 Then Throw New InvalidDataException("ハンドシェイクが不正です")
        Dim pubA = m1(0)
        Dim nonceA = m1(1)
        Dim ephA = m1(2)
        Dim sigA = m1(3)
        If nonceA.Length <> 32 Then Throw New InvalidDataException("Nonceが不正です")

        Dim dsaA = ImportPublic(pubA)
        Try
            If Not dsaA.VerifyData(Pack(LabelA, nonceB, nonceA, ephA), sigA, HashAlgorithmName.SHA256) Then
                Throw New CryptographicException("相手の署名検証に失敗しました")
            End If

            ' 3) 応答側 → 接続側 : 一時ECDH公開鍵 + 署名
            Using eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)
                Dim ephB = eph.ExportSubjectPublicKeyInfo()
                Dim sigB = Self.Dsa.SignData(Pack(LabelB, nonceA, nonceB, ephB, ephA), HashAlgorithmName.SHA256)
                WriteFrame(ns, Pack(ephB, sigB))
                Return New Session() With {
                    .PeerId = IdFromPublicKey(pubA),      ' 認証済みの相手のID
                    .PeerPublicKey = pubA,
                    .PeerDsa = dsaA,
                    .Key = DeriveKey(eph, ephA, nonceA, nonceB)}
            End Using
        Catch
            dsaA.Dispose()
            Throw
        End Try
    End Function

    ''' <summary>接続した側(接続側)のハンドシェイク。接続先の公開鍵が期待する実効IDと一致することを確認してから認証する</summary>
    Private Function InitiatorHandshake(ns As Stream, expectedPeerId As String) As Session
        ' 1) 応答側 → 接続側 : 公開鍵 + Nonce
        Dim m0 = Unpack(ReadFrame(ns))
        If m0.Count <> 2 Then Throw New InvalidDataException("ハンドシェイクが不正です")
        Dim pubB = m0(0)
        Dim nonceB = m0(1)
        If nonceB.Length <> 32 Then Throw New InvalidDataException("Nonceが不正です")

        ' 公開鍵から実効IDを作って期待する相手と照合 (表示名では判断しない)
        If IdFromPublicKey(pubB) <> expectedPeerId Then
            Throw New CryptographicException("相手の公開鍵が宛先の実効IDと一致しません(なりすましの可能性)")
        End If

        Dim dsaB = ImportPublic(pubB)
        Try
            Using eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)
                ' 2) 接続側 → 応答側 : 公開鍵 / Nonce / 一時ECDH公開鍵 / 署名
                Dim ephA = eph.ExportSubjectPublicKeyInfo()
                Dim nonceA = RandomNumberGenerator.GetBytes(32)
                Dim sigA = Self.Dsa.SignData(Pack(LabelA, nonceB, nonceA, ephA), HashAlgorithmName.SHA256)
                WriteFrame(ns, Pack(Self.PublicKey, nonceA, ephA, sigA))

                ' 3) 応答側 → 接続側 : 一時ECDH公開鍵 + 署名 (相手が秘密鍵を持つことの証明)
                Dim m2 = Unpack(ReadFrame(ns))
                If m2.Count <> 2 Then Throw New InvalidDataException("ハンドシェイクが不正です")
                Dim ephB = m2(0)
                Dim sigB = m2(1)
                If Not dsaB.VerifyData(Pack(LabelB, nonceA, nonceB, ephB, ephA), sigB, HashAlgorithmName.SHA256) Then
                    Throw New CryptographicException("相手の署名検証に失敗しました")
                End If
                Return New Session() With {
                    .PeerId = expectedPeerId,
                    .PeerPublicKey = pubB,
                    .PeerDsa = dsaB,
                    .Key = DeriveKey(eph, ephB, nonceA, nonceB)}
            End Using
        Catch
            dsaB.Dispose()
            Throw
        End Try
    End Function

    ' ====================== メッセージのパケット ======================

    ''' <summary>メッセージ1通分のパケットを作る(暗号化 + 署名)。Send と予約の受け渡しで共通</summary>
    Private Function BuildPacket(key As Byte(), toId As String, body As Byte()) As Byte()
        Dim pVer = New Byte() {ProtocolVersion}
        Dim pName = Encoding.UTF8.GetBytes(CleanDisplayName(Self.DisplayName))     ' 相手に伝える自分の表示名
        Dim pFrom = Encoding.UTF8.GetBytes(Self.Id)
        Dim pTo = Encoding.UTF8.GetBytes(toId)
        Dim pTs = Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString(TimeFormat, CultureInfo.InvariantCulture))
        Dim pSeq = BitConverter.GetBytes(NextSeq())
        Dim nonce = RandomNumberGenerator.GetBytes(12)
        Dim cipher(body.Length - 1) As Byte
        Dim tag(15) As Byte
        Using aes As New AesGcm(key, 16)
            aes.Encrypt(nonce, body, cipher, tag, Pack(pVer, pFrom, pTo, pTs, pSeq))
        End Using
        Dim sig = Self.Dsa.SignData(Pack(pVer, pFrom, pTo, pTs, pSeq, nonce, cipher, tag, pName), HashAlgorithmName.SHA256)
        Return Pack(pVer, pFrom, pTo, pTs, pSeq, nonce, cipher, tag, sig, pName)
    End Function

    ''' <summary>受け取ったパケット1通を検証して復号する。s.PeerId = 認証済みの送信者。受信と、予約の受け取りで共通</summary>
    Private Sub ProcessPacket(pk As List(Of Byte()), s As Session, ByRef text As String, ByRef ev As Evidence)
        If pk.Count < 9 OrElse pk(0).Length <> 1 Then Throw New InvalidDataException("パケットが不正です")
        Dim ver = pk(0)(0)
        If ver = ProtocolVersion Then
            If pk.Count <> 10 OrElse pk(9).Length > 400 Then Throw New InvalidDataException("パケットが不正です")
        ElseIf ver = LegacyVersion Then
            If pk.Count <> 9 Then Throw New InvalidDataException("パケットが不正です")
        Else
            Throw New InvalidDataException("未対応のバージョンです")
        End If
        Dim fromId = s.PeerId
        Dim pFrom = Encoding.UTF8.GetString(pk(1))
        Dim pTo = Encoding.UTF8.GetString(pk(2))
        Dim pTs = Encoding.UTF8.GetString(pk(3))
        If pk(4).Length <> 8 Then Throw New InvalidDataException("SequenceNoが不正です")
        Dim seq = BitConverter.ToInt64(pk(4), 0)
        Dim nonce = pk(5)
        Dim cipher = pk(6)
        Dim tag = pk(7)
        Dim sig = pk(8)
        ' 署名の対象: バージョン2は表示名(pk(9))も含める。表示名を後から書き換えても署名検証で見つかる
        Dim signedData = If(ver = ProtocolVersion,
                            Pack(pk(0), pk(1), pk(2), pk(3), pk(4), nonce, cipher, tag, pk(9)),
                            Pack(pk(0), pk(1), pk(2), pk(3), pk(4), nonce, cipher, tag))

        If pFrom <> fromId Then Throw New CryptographicException("FromIDが認証済みの相手と一致しません")
        If pTo <> Self.Id Then Throw New InvalidDataException("宛先IDが自分ではありません")
        If nonce.Length <> 12 OrElse tag.Length <> 16 Then Throw New InvalidDataException("暗号パラメータが不正です")

        Dim ts As DateTime
        If Not DateTime.TryParseExact(pTs, TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal Or DateTimeStyles.AdjustToUniversal, ts) Then
            Throw New InvalidDataException("Timestampの形式が不正です")
        End If
        If Math.Abs((DateTime.UtcNow - ts).TotalSeconds) > MaxSkewSeconds Then
            Throw New InvalidDataException("Timestampが許容範囲外です(リプレイの可能性)")
        End If

        ' 署名検証 (Version, FromID, ToID, Timestamp, SequenceNo, Nonce, EncryptedData, Tag [, 表示名])
        If Not s.PeerDsa.VerifyData(signedData, sig, HashAlgorithmName.SHA256) Then
            Throw New CryptographicException("メッセージの署名検証に失敗しました")
        End If

        SyncLock _lock
            Dim last As Long
            If _lastSeq.TryGetValue(fromId, last) AndAlso seq <= last Then
                Throw New InvalidDataException("SequenceNoが古いため破棄しました(リプレイの可能性)")
            End If
        End SyncLock

        Dim plain(cipher.Length - 1) As Byte
        Using aes As New AesGcm(s.Key, 16)
            aes.Decrypt(nonce, cipher, tag, plain, Pack(pk(0), pk(1), pk(2), pk(3), pk(4)))
        End Using

        SyncLock _lock
            _lastSeq(fromId) = seq
            SaveSession()
        End SyncLock

        text = Encoding.UTF8.GetString(plain)
        ' 後から署名と本文を再検証できるよう、署名の証拠(パケット・差出人の公開鍵・復号に使った鍵)を残す
        ev = Evidence.Create(pk, s.PeerPublicKey, s.Key)
    End Sub

    ' ====================== 送信 ======================

    ''' <summary>相手(IPアドレス:ポート)へ接続する。つながらなければ例外</summary>
    Private Function Connect(host As String, port As Integer) As TcpClient
        Dim cl As New TcpClient()
        Try
            cl.ReceiveTimeout = IoTimeout
            cl.SendTimeout = IoTimeout
            Try
                If Not cl.ConnectAsync(host, port).Wait(ConnectTimeout) Then
                    Throw New TimeoutException("接続がタイムアウトしました(相手が起動していない/ポート未開放の可能性)")
                End If
            Catch ae As AggregateException
                Throw New IOException("接続できません: " & ae.GetBaseException().Message)
            End Try
            Return cl
        Catch
            cl.Dispose()
            Throw
        End Try
    End Function

    ''' <summary>メッセージの本文として送れる大きさか確認する</summary>
    Public Shared Sub CheckTextSize(text As String)
        If Encoding.UTF8.GetByteCount(text) > MaxText Then Throw New ArgumentException("メッセージが長すぎます(最大64KB)")
    End Sub

    ''' <summary>相手(IPアドレス:ポート)へ直接接続してメッセージを1通送る。失敗時は例外</summary>
    Public Sub Send(toId As String, host As String, port As Integer, text As String)
        CheckTextSize(text)
        Dim body = Encoding.UTF8.GetBytes(text)

        Using cl = Connect(host, port)
            Using ns = cl.GetStream()
                Using s = InitiatorHandshake(ns, toId)
                    WriteFrame(ns, BuildPacket(s.Key, toId, body))
                    Dim ack = ReadFrame(ns)
                    If ack.Length <> 1 OrElse ack(0) <> 1 Then Throw New IOException("相手に受理されませんでした")
                End Using
            End Using
        End Using
    End Sub

    ' ====================== 予約の問い合わせ(受け取る側) ======================

    ''' <summary>
    ''' 相手(IPアドレス:ポート)へ接続して、自分宛の予約メッセージを受け取る。受け取った件数を返す。
    ''' 受け取ったメッセージは、通常の受信と同じ MessageReceived で通知される。
    ''' 自分の側でポートを開けておく必要はない(外向きの接続だけで受け取れる)。
    ''' </summary>
    Public Function Pull(peerId As String, host As String, port As Integer) As Integer
        Using cl = Connect(host, port)
            Using ns = cl.GetStream()
                Using s = InitiatorHandshake(ns, peerId)
                    WriteFrame(ns, Pack(New Byte() {OpPull}, BitConverter.GetBytes(IPAddress.HostToNetworkOrder(Math.Max(1, PullBatch)))))
                    Dim head = Unpack(ReadFrame(ns))
                    If head.Count <> 2 OrElse head(0).Length <> 1 OrElse head(0)(0) <> OpPullReply OrElse head(1).Length <> 4 Then
                        Throw New InvalidDataException("相手は予約の問い合わせに対応していません")
                    End If
                    Dim n = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(head(1), 0))
                    If n < 0 OrElse n > Math.Max(1, PullBatch) Then Throw New InvalidDataException("予約の件数が不正です")

                    Dim got = 0
                    For i = 1 To n
                        Dim text As String = Nothing
                        Dim ev As Evidence = Nothing
                        Try
                            ProcessPacket(Unpack(ReadFrame(ns)), s, text, ev)
                        Catch
                            Try
                                WriteFrame(ns, New Byte() {0})
                            Catch
                            End Try
                            Throw
                        End Try
                        WriteFrame(ns, New Byte() {1})
                        got += 1
                        RaiseEvent MessageReceived(s.PeerId, text, ev)
                    Next
                    Return got
                End Using
            End Using
        End Using
    End Function

    ' ====================== 補助 ======================

    ''' <summary>P-256公開鍵(SubjectPublicKeyInfo)を読み込む</summary>
    Public Shared Function ImportPublic(spki As Byte()) As ECDsa
        Dim dsa = ECDsa.Create()
        Try
            Dim read As Integer
            dsa.ImportSubjectPublicKeyInfo(spki, read)
            If read <> spki.Length OrElse dsa.KeySize <> 256 Then Throw New CryptographicException("公開鍵の形式が不正です")
            Return dsa
        Catch
            dsa.Dispose()
            Throw
        End Try
    End Function

    ''' <summary>一時ECDHの共有秘密 + 両Nonce から AES-256 鍵(32バイト)を導出</summary>
    Private Shared Function DeriveKey(mine As ECDiffieHellman, theirSpki As Byte(), nonceA As Byte(), nonceB As Byte()) As Byte()
        Using other = ECDiffieHellman.Create()
            Dim read As Integer
            other.ImportSubjectPublicKeyInfo(theirSpki, read)
            If read <> theirSpki.Length OrElse other.KeySize <> 256 Then Throw New CryptographicException("ECDH公開鍵が不正です")
            Return mine.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256, Pack(nonceA, nonceB), Nothing)
        End Using
    End Function

    ''' <summary>単調増加するSequenceNo (UNIXミリ秒ベース。再起動しても後戻りしない)</summary>
    Private Function NextSeq() As Long
        SyncLock _lock
            Dim ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            _lastSent = Math.Max(ms, _lastSent + 1)
            Return _lastSent
        End SyncLock
    End Function

    ''' <summary>Session.dat : 相手ID と 最後のSequenceNo</summary>
    Private Sub LoadSession()
        If Not File.Exists(_sessionPath) Then Return
        For Each line In File.ReadAllLines(_sessionPath, Encoding.UTF8)
            Dim p = line.Split(ControlChars.Tab)
            Dim v As Long
            If p.Length = 2 AndAlso Long.TryParse(p(1), v) Then _lastSeq(p(0)) = v
        Next
    End Sub

    Private Sub SaveSession()
        Try
            File.WriteAllLines(_sessionPath, _lastSeq.Select(Function(kv) kv.Key & vbTab & kv.Value.ToString()).ToArray(), Encoding.UTF8)
        Catch
        End Try
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        StopListening()
    End Sub

End Class
