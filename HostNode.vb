Option Strict On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks

' =====================================================================
' ホスト機能(ネームサーバー役)
'   ホスト : 「実効ID → IP:ポート」の登録を預かり、問い合わせに答える
'   クライアント : 受信中に自分の情報をホストへ通知し、送信時に宛先をホストへ問い合わせる
' ホストの回答は参考情報。最終的な本人確認は、これまで通り送受信端末どうしの認証で行う。
' プロトコル(1要求=1接続):
'   ホスト→相手 : Nonce(32バイト)
'   相手→ホスト : 要求 (Pack形式)  ... 登録のみ Nonce を含めて署名する(リプレイ対策)
'   ホスト→相手 : 応答 (Pack形式。先頭パートが状態 1=成功/0=失敗)
' =====================================================================

Public Module HostProto
    Public Const OpRegister As Byte = 1   ' 登録 : [op, 公開鍵, メッセージポート, ホストポート, 署名]
    Public Const OpLookup As Byte = 2     ' 検索 : [op, 実効ID]
    Public Const OpHosts As Byte = 3      ' ホスト一覧 : [op]
    Public ReadOnly LabelReg As Byte() = Encoding.ASCII.GetBytes("P2PHOST1-REGISTER")

    ''' <summary>ランダムな順に並べ替えた新しい一覧を返す(Fisher-Yates)</summary>
    Public Function Shuffle(Of T)(source As IEnumerable(Of T)) As List(Of T)
        Dim list = source.ToList()
        For i = list.Count - 1 To 1 Step -1
            Dim j = Random.Shared.Next(i + 1)
            Dim tmp = list(i)
            list(i) = list(j)
            list(j) = tmp
        Next
        Return list
    End Function
End Module

''' <summary>
''' Peers.ini : [Host] この端末のホスト機能設定 / [Hosts] 接続を試みるホストの一覧。
''' 初回起動時はテンプレートを作る。ホストから教えられた他ホストはここへ追記される。
''' </summary>
Public Class PeerList

    ' ---- [Network] 通信の設定。Peers.ini を編集して調整する(変更は再起動後に反映) ----
    Public Property NotifyHosts As Integer = 15              ' 自分の情報を通知するホストの台数(通知のたびにランダムに選ぶ)
    Public Property NotifyIntervalMinutes As Integer = 12    ' 通知の間隔(分)
    Public Property QueryHosts As Integer = 45               ' 宛先の問い合わせに使うホストの台数の上限(ランダムな順で問い合わせる)
    Public Property QueryParallel As Integer = 5             ' 同時に問い合わせる(通知する)ホストの数
    Public Property MaxKnownHosts As Integer = 200           ' 覚えておくホストの上限
    Public Property AddressCacheMinutes As Integer = 30      ' ホストで見つけた相手のアドレスを覚えておく時間(分)。0で無効
    ' ---- [Host] ホスト機能の設定 ----
    Public Property RegistrationTtlMinutes As Integer = 35   ' 登録の保持時間(分)。クライアントの通知間隔の3倍程度が目安
    Public Property MaxRegistrations As Integer = 20000      ' 保持する登録数の上限(超えたら、期限が近いものから押し出す)

    Private ReadOnly _path As String
    Private ReadOnly _hosts As New List(Of String)
    Private ReadOnly _lock As New Object()

    Public Property LogMaxLines As Integer = 1000     ' 画面のログに残す最大行数(超えたら古いものから消す)
    ' ---- [Reserve] 送信予約・予約の問い合わせ ----
    Public Property PollEnabled As Boolean            ' 起動時に、予約メッセージの問い合わせを自動で再開するか
    Public Property PollIntervalMinutes As Integer = 5       ' 連絡先への問い合わせの間隔(分)
    Public Property ReservationExpireDays As Integer = 30    ' 予約を保管する日数(0なら期限なし)
    Public Property MaxReserved As Integer = 1000            ' 保管できる予約の最大件数
    Public Property PullBatch As Integer = 50                ' 問い合わせ1回で受け渡す最大件数
    Public Property ListenEnabled As Boolean          ' 起動時に自動でメッセージ受信を開始するか
    Public Property ListenPort As Integer = 50000     ' メッセージ受信ポート(再起動後も同じ番号を使う)
    Public Property HostEnabled As Boolean
    Public Property HostPort As Integer = 50001

    Private Sub New(path As String)
        _path = path
    End Sub

    Public Shared Function Load(path As String) As PeerList
        Dim pl As New PeerList(path)
        If Not File.Exists(path) Then
            pl.Save()
            Return pl
        End If

        Dim section = ""
        Dim pending As New List(Of String)
        For Each raw In File.ReadAllLines(path, Encoding.UTF8)
            Dim line = raw.Trim()
            If line.Length = 0 OrElse line.StartsWith(";") OrElse line.StartsWith("#") Then Continue For
            If line.StartsWith("[") AndAlso line.EndsWith("]") Then
                section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant()
                Continue For
            End If
            Dim eq = line.IndexOf("="c)
            If eq <= 0 Then Continue For
            Dim key = line.Substring(0, eq).Trim().ToLowerInvariant()
            Dim value = line.Substring(eq + 1).Trim()

            If section = "messenger" Then
                If key = "enabled" Then pl.ListenEnabled = (value = "1" OrElse value.Equals("true", StringComparison.OrdinalIgnoreCase))
                If key = "port" Then
                    Dim lp As Integer
                    If Integer.TryParse(value, lp) AndAlso lp >= 1 AndAlso lp <= 65535 Then pl.ListenPort = lp
                End If
            ElseIf section = "host" Then
                If key = "enabled" Then pl.HostEnabled = (value = "1" OrElse value.Equals("true", StringComparison.OrdinalIgnoreCase))
                If key = "port" Then
                    Dim p As Integer
                    If Integer.TryParse(value, p) AndAlso p >= 1 AndAlso p <= 65535 Then pl.HostPort = p
                End If
                If key = "registrationttlminutes" Then pl.RegistrationTtlMinutes = ClampInt(value, 1, 10080, pl.RegistrationTtlMinutes)
                If key = "maxregistrations" Then pl.MaxRegistrations = ClampInt(value, 100, 1000000, pl.MaxRegistrations)
            ElseIf section = "reserve" Then
                If key = "pollenabled" Then pl.PollEnabled = (value = "1" OrElse value.Equals("true", StringComparison.OrdinalIgnoreCase))
                If key = "pollintervalminutes" Then pl.PollIntervalMinutes = ClampInt(value, 1, 1440, pl.PollIntervalMinutes)
                If key = "reservationexpiredays" Then pl.ReservationExpireDays = ClampInt(value, 0, 3650, pl.ReservationExpireDays)
                If key = "maxreserved" Then pl.MaxReserved = ClampInt(value, 10, 100000, pl.MaxReserved)
                If key = "pullbatch" Then pl.PullBatch = ClampInt(value, 1, 500, pl.PullBatch)
            ElseIf section = "log" Then
                If key = "maxlines" Then pl.LogMaxLines = ClampInt(value, 10, 100000, pl.LogMaxLines)
            ElseIf section = "network" Then
                If key = "notifyhosts" Then pl.NotifyHosts = ClampInt(value, 1, 200, pl.NotifyHosts)
                If key = "notifyintervalminutes" Then pl.NotifyIntervalMinutes = ClampInt(value, 1, 1440, pl.NotifyIntervalMinutes)
                If key = "queryhosts" Then pl.QueryHosts = ClampInt(value, 1, 1000, pl.QueryHosts)
                If key = "queryparallel" Then pl.QueryParallel = ClampInt(value, 1, 50, pl.QueryParallel)
                If key = "maxknownhosts" Then pl.MaxKnownHosts = ClampInt(value, 1, 5000, pl.MaxKnownHosts)
                If key = "addresscacheminutes" Then pl.AddressCacheMinutes = ClampInt(value, 0, 1440, pl.AddressCacheMinutes)
            ElseIf section = "hosts" AndAlso key = "host" Then
                pending.Add(value)     ' 上限(MaxKnownHosts)を読み込んでから追加する
            End If
        Next
        For Each h In pending
            pl.AddHost(h, persist:=False)
        Next
        Return pl
    End Function

    ''' <summary>数値を範囲内に収める。数値でなければ既定値</summary>
    Private Shared Function ClampInt(value As String, min As Integer, max As Integer, fallback As Integer) As Integer
        Dim n As Integer
        If Integer.TryParse(value, n) Then Return Math.Max(min, Math.Min(max, n))
        Return fallback
    End Function

    Public Function HostsSnapshot() As List(Of String)
        SyncLock _lock
            Return _hosts.ToList()
        End SyncLock
    End Function

    ''' <summary>
    ''' ホストを追加する。ipOnly=True(他人から教えられた情報)の場合はIPアドレスのみ受け付ける。
    ''' INIを壊す文字が入らないよう、ホスト部は英数字と . - : のみ許可する。
    ''' </summary>
    Public Function AddHost(endpoint As String, Optional persist As Boolean = True, Optional ipOnly As Boolean = False) As Boolean
        Dim ep As (Host As String, Port As Integer)
        Try
            ep = ParseEndpoint(endpoint)
        Catch ex As FormatException
            Return False
        End Try
        If Not ep.Host.All(Function(ch) Char.IsAsciiLetterOrDigit(ch) OrElse ch = "."c OrElse ch = "-"c OrElse ch = ":"c) Then Return False
        Dim addr As IPAddress = Nothing
        If ipOnly AndAlso Not IPAddress.TryParse(ep.Host, addr) Then Return False

        Dim norm = $"{ep.Host}:{ep.Port}"
        SyncLock _lock
            If _hosts.Count >= MaxKnownHosts OrElse _hosts.Contains(norm, StringComparer.OrdinalIgnoreCase) Then Return False
            _hosts.Add(norm)
        End SyncLock
        If persist Then Save()
        Return True
    End Function

    Public Sub Save()
        SyncLock _lock
            Dim sb As New StringBuilder()
            sb.AppendLine("; P2P Messenger ピア設定")
            sb.AppendLine("; [Messenger] : メッセージ受信の設定 (Enabled=1 なら起動時に受信を開始。ポート番号は画面で変更すると自動で記録される)")
            sb.AppendLine("; [Host]  : この端末でホスト機能を提供する設定 (Enabled=1 で有効)")
            sb.AppendLine("; [Reserve] : 送信予約と、予約メッセージの問い合わせの設定")
            sb.AppendLine("; [Log] : 画面のログの最大行数")
            sb.AppendLine("; [Network] : 通信の設定。ネットワークの規模に合わせて調整する(変更は再起動後に反映。編集はアプリを終了してから)")
            sb.AppendLine("; [Hosts] : 接続を試みるホストの一覧。Host=IPアドレス:ポート を1行ずつ書く")
            sb.AppendLine("[Messenger]")
            sb.AppendLine("Enabled=" & If(ListenEnabled, "1", "0"))
            sb.AppendLine("Port=" & ListenPort.ToString())
            sb.AppendLine()
            sb.AppendLine("[Host]")
            sb.AppendLine("Enabled=" & If(HostEnabled, "1", "0"))
            sb.AppendLine("Port=" & HostPort.ToString())
            sb.AppendLine("; 登録の保持時間(分)。クライアントの通知間隔(NotifyIntervalMinutes)の3倍程度にする")
            sb.AppendLine("RegistrationTtlMinutes=" & RegistrationTtlMinutes.ToString())
            sb.AppendLine("; 保持する登録数の上限。超えると、期限が近いものから押し出す")
            sb.AppendLine("MaxRegistrations=" & MaxRegistrations.ToString())
            sb.AppendLine()
            sb.AppendLine("[Network]")
            sb.AppendLine("; 自分の情報を通知するホストの台数(通知のたびにランダムに選ぶ)。多いほど相手に見つけてもらいやすい")
            sb.AppendLine("NotifyHosts=" & NotifyHosts.ToString())
            sb.AppendLine("; 通知の間隔(分)。長いほどホストの負荷は減るが、アドレス変更の反映が遅れる")
            sb.AppendLine("NotifyIntervalMinutes=" & NotifyIntervalMinutes.ToString())
            sb.AppendLine("; 宛先の問い合わせに使うホストの台数の上限(ランダムな順)。ホストが多いネットワークでは増やす")
            sb.AppendLine("QueryHosts=" & QueryHosts.ToString())
            sb.AppendLine("; 同時に問い合わせる(通知する)ホストの数")
            sb.AppendLine("QueryParallel=" & QueryParallel.ToString())
            sb.AppendLine("; 覚えておくホストの上限")
            sb.AppendLine("MaxKnownHosts=" & MaxKnownHosts.ToString())
            sb.AppendLine("; ホストで見つけた相手のアドレスを、メモリに覚えておく時間(分)。0で無効。覚えているアドレスには、ホストへ問い合わせる前にまず接続する")
            sb.AppendLine("AddressCacheMinutes=" & AddressCacheMinutes.ToString())
            sb.AppendLine()
            sb.AppendLine("[Reserve]")
            sb.AppendLine("; 予約メッセージの問い合わせを自動で行うか(画面のチェックの状態。1で、起動時に再開する)")
            sb.AppendLine("PollEnabled=" & If(PollEnabled, "1", "0"))
            sb.AppendLine("; 連絡先に「自分宛の予約はありますか」と問い合わせる間隔(分)")
            sb.AppendLine("PollIntervalMinutes=" & PollIntervalMinutes.ToString())
            sb.AppendLine("; 予約を保管する日数。過ぎた予約は削除される(0なら期限なし)")
            sb.AppendLine("ReservationExpireDays=" & ReservationExpireDays.ToString())
            sb.AppendLine("; 保管できる予約の最大件数")
            sb.AppendLine("MaxReserved=" & MaxReserved.ToString())
            sb.AppendLine("; 問い合わせ1回で渡す(受け取る)最大件数")
            sb.AppendLine("PullBatch=" & PullBatch.ToString())
            sb.AppendLine()
            sb.AppendLine("[Log]")
            sb.AppendLine("; 画面のログに残す最大行数(10〜100000)。超えると古いものから消える")
            sb.AppendLine("MaxLines=" & LogMaxLines.ToString())
            sb.AppendLine()
            sb.AppendLine("[Hosts]")
            If _hosts.Count = 0 Then sb.AppendLine("; Host=203.0.113.5:50001")
            For Each h In _hosts
                sb.AppendLine("Host=" & h)
            Next
            Try
                File.WriteAllText(_path, sb.ToString(), Encoding.UTF8)
            Catch
            End Try
        End SyncLock
    End Sub

End Class

''' <summary>ホスト役。登録を預かり、検索・ホスト一覧の問い合わせに答える(メッセージ送受信とは別ポート)</summary>
Public Class HostService
    Implements IDisposable

    Public Event Info(message As String)

    Private Const MaxActive As Integer = 32
    Private Const MaxHostsReply As Integer = 50
    Private Const IoTimeout As Integer = 5000
    Private Shared ReadOnly PurgeInterval As TimeSpan = TimeSpan.FromSeconds(30)

    Private Class Rec
        Public Endpoint As String = ""
        Public Expires As DateTime
    End Class

    Private ReadOnly _peers As PeerList
    Private ReadOnly _registry As New Dictionary(Of String, Rec)
    Private ReadOnly _lock As New Object()
    Private _listener As TcpListener
    Private _cts As CancellationTokenSource
    Private _active As Integer
    Private _lastPurge As DateTime = DateTime.MinValue

    Public Sub New(peers As PeerList)
        _peers = peers
    End Sub

    Public Sub StartService(port As Integer)
        If _listener IsNot Nothing Then Return
        Dim l As New TcpListener(IPAddress.Any, port)
        l.Start()
        _listener = l
        _cts = New CancellationTokenSource()
        Dim ct = _cts.Token
        Dim t = Task.Run(Function() AcceptLoop(l, ct))
    End Sub

    Public Sub StopService()
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
                    Dim t = Task.Run(Sub() Handle(c))
                End If
            End While
        Catch ex As OperationCanceledException
        Catch ex As ObjectDisposedException
        Catch ex As Exception
            RaiseEvent Info("ホスト待機エラー: " & ex.Message)
        End Try
    End Function

    Private Sub Handle(cl As TcpClient)
        Dim remote As String = "?"
        Try
            Dim rep = TryCast(cl.Client.RemoteEndPoint, IPEndPoint)
            If rep Is Nothing Then Return
            Dim ip = rep.Address
            If ip.IsIPv4MappedToIPv6 Then ip = ip.MapToIPv4()
            remote = ip.ToString()
            cl.ReceiveTimeout = IoTimeout
            cl.SendTimeout = IoTimeout

            Using ns = cl.GetStream()
                Dim nonce = RandomNumberGenerator.GetBytes(32)
                WriteFrame(ns, nonce)
                Dim req = Unpack(ReadFrame(ns))
                Try
                    If req.Count = 0 OrElse req(0).Length <> 1 Then Throw New InvalidDataException("要求が不正です")
                    Select Case req(0)(0)
                        Case OpRegister
                            DoRegister(ns, req, nonce, ip)
                        Case OpLookup
                            DoLookup(ns, req)
                        Case OpHosts
                            WriteFrame(ns, HostListReply())
                        Case Else
                            Throw New InvalidDataException("未対応の要求です")
                    End Select
                Catch
                    Try
                        WriteFrame(ns, Pack(New Byte() {0}))
                    Catch
                    End Try
                    Throw
                End Try
            End Using
        Catch ex As Exception
            RaiseEvent Info($"ホスト要求を拒否しました ({remote}): {ex.Message}")
        Finally
            cl.Close()
            Interlocked.Decrement(_active)
        End Try
    End Sub

    ''' <summary>
    ''' 登録。署名(ホストが出したNonceを含む)で秘密鍵の所有を確認し、実効IDは公開鍵から作る。
    ''' IPアドレスは申告ではなく実際の接続元を使う。
    ''' </summary>
    Private Sub DoRegister(ns As Stream, req As List(Of Byte()), nonce As Byte(), ip As IPAddress)
        If req.Count <> 5 Then Throw New InvalidDataException("登録要求が不正です")
        Dim pub = req(1)
        Dim portB = req(2)
        Dim hostB = req(3)
        Dim sig = req(4)
        If portB.Length <> 4 OrElse hostB.Length <> 4 Then Throw New InvalidDataException("ポート指定が不正です")
        Dim port = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(portB, 0))
        Dim hostPort = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(hostB, 0))
        If port < 0 OrElse port > 65535 OrElse hostPort < 0 OrElse hostPort > 65535 OrElse (port = 0 AndAlso hostPort = 0) Then
            Throw New InvalidDataException("ポート番号が不正です")
        End If

        Using dsa = Messenger.ImportPublic(pub)
            If Not dsa.VerifyData(Pack(LabelReg, nonce, pub, portB, hostB), sig, HashAlgorithmName.SHA256) Then
                Throw New CryptographicException("登録の署名検証に失敗しました")
            End If
        End Using
        Dim id = IdFromPublicKey(pub)

        If port > 0 Then
            SyncLock _lock
                Dim utc = DateTime.UtcNow
                If utc - _lastPurge > PurgeInterval Then
                    PurgeExpired()
                    _lastPurge = utc
                End If
                ' 上限に達していたら、まず期限切れを整理し、それでも足りなければ期限が最も近い登録を押し出す
                If Not _registry.ContainsKey(id) AndAlso _registry.Count >= _peers.MaxRegistrations Then
                    PurgeExpired()
                    _lastPurge = utc
                    If _registry.Count >= _peers.MaxRegistrations Then EvictSoonestExpiring()
                End If
                _registry(id) = New Rec() With {.Endpoint = ip.ToString() & ":" & port.ToString(),
                                                .Expires = utc + TimeSpan.FromMinutes(_peers.RegistrationTtlMinutes)}
            End SyncLock
        End If
        ' 相手もホスト役なら、ホスト一覧に加える (接続元IP + 申告されたホストポート)
        If hostPort > 0 Then _peers.AddHost(ip.ToString() & ":" & hostPort.ToString(), ipOnly:=True)

        WriteFrame(ns, HostListReply())
    End Sub

    Private Sub DoLookup(ns As Stream, req As List(Of Byte()))
        If req.Count <> 2 OrElse req(1).Length > 64 Then Throw New InvalidDataException("検索要求が不正です")
        Dim id = Encoding.UTF8.GetString(req(1))
        Dim endpoint As String = Nothing
        SyncLock _lock
            Dim r As Rec = Nothing
            If _registry.TryGetValue(id, r) AndAlso r.Expires > DateTime.UtcNow Then endpoint = r.Endpoint
        End SyncLock
        If endpoint IsNot Nothing Then
            WriteFrame(ns, Pack(New Byte() {1}, Encoding.UTF8.GetBytes(endpoint)))
        Else
            WriteFrame(ns, Pack(New Byte() {0}))
        End If
    End Sub

    Private Function HostListReply() As Byte()
        Dim parts As New List(Of Byte()) From {New Byte() {1}}
        ' 先頭から固定で返すと一覧の後ろのホストが広まらないので、ランダムに選んで返す
        parts.AddRange(Shuffle(_peers.HostsSnapshot()).Take(MaxHostsReply).Select(Function(h) Encoding.UTF8.GetBytes(h)))
        Return Pack(parts.ToArray())
    End Function

    Private Sub EvictSoonestExpiring()
        Dim victim As String = Nothing
        Dim soonest = DateTime.MaxValue
        For Each kv In _registry
            If kv.Value.Expires < soonest Then
                soonest = kv.Value.Expires
                victim = kv.Key
            End If
        Next
        If victim IsNot Nothing Then _registry.Remove(victim)
    End Sub

    Private Sub PurgeExpired()
        Dim utc = DateTime.UtcNow
        Dim dead = _registry.Where(Function(kv) kv.Value.Expires <= utc).Select(Function(kv) kv.Key).ToList()
        For Each k In dead
            _registry.Remove(k)
        Next
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        StopService()
    End Sub

End Class

''' <summary>ホストを利用する側。自分の情報の通知、宛先の検索、ホスト一覧の取得、ホスト経由を含む送信</summary>
''' <summary>ホストが答えた宛先の候補(Endpoint = IP:ポート, Host = 答えたホスト)</summary>
Public Class HostCandidate
    Public Property Endpoint As String = ""
    Public Property Host As String = ""
End Class

Public Class HostClient

    Public Event Info(message As String)

    Private Const ConnectTimeout As Integer = 3000
    Private Const IoTimeout As Integer = 5000
    Private Const MaxAttempts As Integer = 15       ' 1回の処理で試すホスト数の上限

    Private ReadOnly _self As Identity
    Private ReadOnly _peers As PeerList
    Private _lastStatus As String = ""

    Public Sub New(self As Identity, peers As PeerList)
        _self = self
        _peers = peers
    End Sub

    Public ReadOnly Property HasHosts As Boolean
        Get
            Return _peers.HostsSnapshot().Count > 0
        End Get
    End Property

    ''' <summary>ホストへ接続し、Nonceを受け取って要求を送り、応答を返す</summary>
    Private Function Exchange(endpoint As String, buildRequest As Func(Of Byte(), Byte())) As List(Of Byte())
        Dim ep = ParseEndpoint(endpoint)
        Using cl As New TcpClient()
            cl.ReceiveTimeout = IoTimeout
            cl.SendTimeout = IoTimeout
            Try
                If Not cl.ConnectAsync(ep.Host, ep.Port).Wait(ConnectTimeout) Then Throw New TimeoutException("接続がタイムアウトしました")
            Catch ae As AggregateException
                Throw New IOException(ae.GetBaseException().Message)
            End Try
            Using ns = cl.GetStream()
                Dim nonce = ReadFrame(ns)
                If nonce.Length <> 32 Then Throw New InvalidDataException("ホストの応答が不正です")
                WriteFrame(ns, buildRequest(nonce))
                Return Unpack(ReadFrame(ns))
            End Using
        End Using
    End Function

    Private Shared Function IsOk(r As List(Of Byte())) As Boolean
        Return r.Count >= 1 AndAlso r(0).Length = 1 AndAlso r(0)(0) = 1
    End Function

    ''' <summary>
    ''' 自分の情報をホストへ通知する。通知先は一覧からランダムに選び、NotifyHosts 台に成功するまで、
    ''' QueryParallel 台ずつ並行して試す。msgPort=0なら受信の通知はしない(ホスト役としてのみ通知)。成功したホスト数を返す。
    ''' </summary>
    Public Function RegisterAll(msgPort As Integer, hostPort As Integer) As Integer
        Dim hosts = Shuffle(_peers.HostsSnapshot())
        If hosts.Count = 0 Then
            ReportStatusChange("ホストが設定されていません。Peers.ini の [Hosts] に Host=IPアドレス:ポート を追加してください。")
            Return 0
        End If

        Dim want = _peers.NotifyHosts
        Dim attemptLimit = Math.Max(MaxAttempts, want * 3)    ' つながらないホストがあっても、ある程度は次を試す
        Dim ok = 0
        Dim idx = 0
        While ok < want AndAlso idx < hosts.Count AndAlso idx < attemptLimit
            Dim n = Math.Min(Math.Min(_peers.QueryParallel, want - ok), hosts.Count - idx)
            Dim batch = hosts.GetRange(idx, n)
            idx += n
            Dim tasks = batch.Select(Function(h) Task.Run(Function() TryRegister(h, msgPort, hostPort))).ToArray()
            Task.WaitAll(tasks.Cast(Of Task)().ToArray())
            ok += tasks.Count(Function(task) task.Result)
        End While
        Return ok
    End Function

    Private Function TryRegister(h As String, msgPort As Integer, hostPort As Integer) As Boolean
        Try
            Dim r = Exchange(h, Function(nonce) BuildRegister(nonce, msgPort, hostPort))
            If IsOk(r) Then
                _lastStatus = ""
                RaiseEvent Info($"ホスト {h} に接続し、自分の情報を通知しました。")
                MergeHosts(r)
                Return True
            End If
        Catch ex As Exception
            ReportStatusChange($"ホスト {h} に通知できませんでした: {ex.Message}")
        End Try
        Return False
    End Function

    Private Function BuildRegister(nonce As Byte(), msgPort As Integer, hostPort As Integer) As Byte()
        Dim portB = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(msgPort))
        Dim hostB = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(hostPort))
        Dim sig = _self.Dsa.SignData(Pack(LabelReg, nonce, _self.PublicKey, portB, hostB), HashAlgorithmName.SHA256)
        Return Pack(New Byte() {OpRegister}, _self.PublicKey, portB, hostB, sig)
    End Function

    ''' <summary>
    ''' 宛先の実効IDの現在のIP:ポートを、ホストへ問い合わせる。ホストはランダムな順に、最大 QueryHosts 台まで、
    ''' QueryParallel 台ずつ並行して問い合わせる。見つかったものから順に返す(別のホストが違うアドレスを答えることもある)。
    ''' </summary>
    Public Iterator Function Candidates(id As String, Optional quiet As Boolean = False) As IEnumerable(Of HostCandidate)
        Dim hosts = Shuffle(_peers.HostsSnapshot())
        Dim limit = Math.Min(_peers.QueryHosts, hosts.Count)
        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim reached = 0
        Dim idx = 0
        While idx < limit
            Dim n = Math.Min(_peers.QueryParallel, limit - idx)
            Dim batch = hosts.GetRange(idx, n)
            idx += n
            Dim tasks = batch.Select(Function(h) Task.Run(Function() QueryOne(h, id, quiet))).ToArray()
            Task.WaitAll(tasks.Cast(Of Task)().ToArray())
            For Each task In tasks
                Dim r = task.Result
                If r.Reached Then reached += 1
                If r.Candidate IsNot Nothing AndAlso seen.Add(r.Candidate.Endpoint) Then Yield r.Candidate
            Next
        End While
        If Not quiet Then RaiseEvent Info($"ホスト {limit} 台に問い合わせましたが、使えるアドレスは見つかりませんでした(接続できたのは {reached} 台)。")
    End Function

    Private Class QueryResult
        Public Reached As Boolean
        Public Candidate As HostCandidate
    End Class

    Private Function QueryOne(h As String, id As String, quiet As Boolean) As QueryResult
        Dim res As New QueryResult()
        Try
            Dim r = Exchange(h, Function(nonce) Pack(New Byte() {OpLookup}, Encoding.UTF8.GetBytes(id)))
            res.Reached = True
            If IsOk(r) AndAlso r.Count >= 2 Then
                Dim ep = Encoding.UTF8.GetString(r(1))
                ParseEndpoint(ep)   ' 形式チェック(不正なら例外)
                If Not quiet Then RaiseEvent Info($"ホスト {h} に接続しました: 宛先が見つかりました ({ep})")
                res.Candidate = New HostCandidate() With {.Endpoint = ep, .Host = h}
            End If
        Catch ex As Exception
            If Not quiet Then RaiseEvent Info($"ホスト {h} に問い合わせできませんでした: {ex.Message}")
        End Try
        Return res
    End Function

    ''' <summary>最初につながったホストから他のホストの情報をもらい、Peers.ini に追記する</summary>
    Public Sub RefreshHosts()
        Dim hosts = Shuffle(_peers.HostsSnapshot())
        Dim limit = Math.Min(MaxAttempts, hosts.Count)
        Dim idx = 0
        While idx < limit
            Dim n = Math.Min(_peers.QueryParallel, limit - idx)
            Dim batch = hosts.GetRange(idx, n)
            idx += n
            Dim tasks = batch.Select(Function(h) Task.Run(Function() TryFetchHosts(h))).ToArray()
            Task.WaitAll(tasks.Cast(Of Task)().ToArray())
            If tasks.Any(Function(task) task.Result) Then Return      ' 1台でもつながれば、そこで終了
        End While
    End Sub

    Private Function TryFetchHosts(h As String) As Boolean
        Try
            Dim r = Exchange(h, Function(nonce) Pack(New Byte() {OpHosts}))
            If IsOk(r) Then
                RaiseEvent Info($"ホスト {h} に接続しました(ホスト一覧を取得)。")
                MergeHosts(r)
                Return True
            End If
        Catch ex As Exception
            RaiseEvent Info($"ホスト {h} に接続できませんでした: {ex.Message}")
        End Try
        Return False
    End Function

    Private Sub MergeHosts(r As List(Of Byte()))
        Dim added = 0
        For i = 1 To r.Count - 1
            If _peers.AddHost(Encoding.UTF8.GetString(r(i)), persist:=False, ipOnly:=True) Then added += 1
        Next
        If added > 0 Then
            _peers.Save()
            RaiseEvent Info($"ホストから新しいホスト情報を {added} 件受け取り、Peers.ini に追記しました。")
        End If
    End Sub

    ''' <summary>
    ''' メッセージ送信。宛先への接続は、次の順に試す(つながったところで終わり)。
    '''   1) 連絡先に登録したアドレス
    '''   2) 前回ホストで見つけたアドレス(メモリ上のキャッシュ。ホストへの問い合わせを減らすため)
    '''   3) ホストでの検索(ホストが示したアドレスへ送れなければ、他のホストが示した別のアドレスも順に試す)
    ''' どの場合も、送信時に相手の公開鍵と実効IDを照合する。成功した送信方法の説明を返す。
    ''' </summary>
    Public Function SendMessage(msgr As Messenger, toId As String, manualEndpoint As String, text As String) As String
        Dim lastError As String = Nothing
        Dim tried As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim hasHosts = _peers.HostsSnapshot().Count > 0

        If Not String.IsNullOrWhiteSpace(manualEndpoint) Then
            Try
                Dim ep = ParseEndpoint(manualEndpoint)
                tried.Add($"{ep.Host}:{ep.Port}")
                msgr.Send(toId, ep.Host, ep.Port, text)
                Return $"登録アドレス {ep.Host}:{ep.Port} へ直接送信"
            Catch ex As Exception
                lastError = ex.Message
                If Not hasHosts Then Throw
                RaiseEvent Info($"登録アドレス {manualEndpoint} には送れませんでした({ex.Message})。ホストで検索します。")
            End Try
        ElseIf Not hasHosts Then
            Throw New IOException("連絡先にアドレスがなく、ホストも設定されていません。")
        End If

        ' 前回ホストで見つけたアドレスがあれば、ホストへ問い合わせる前に、まずそこへ送る
        Dim cached = CachedEndpoint(toId)
        If cached IsNot Nothing AndAlso Not tried.Contains(cached) Then
            tried.Add(cached)
            Try
                Dim ce = ParseEndpoint(cached)
                msgr.Send(toId, ce.Host, ce.Port, text)
                RememberEndpoint(toId, cached)          ' 使えたので、覚えておく時間を延ばす
                Return $"前回ホストで見つけたアドレス {cached} へ送信(ホストへの問い合わせなし)"
            Catch ex As Exception
                lastError = ex.Message
                ForgetEndpoint(toId)                     ' 古くなっていたので捨てて、ホストで探し直す
                RaiseEvent Info($"前回見つけたアドレス {cached} には送れませんでした({ex.Message})。ホストで検索します。")
            End Try
        End If

        Dim foundAny = False
        For Each cand In Candidates(toId)
            foundAny = True
            If tried.Contains(cand.Endpoint) Then Continue For      ' すでに試して失敗したアドレス
            tried.Add(cand.Endpoint)
            Try
                Dim fe = ParseEndpoint(cand.Endpoint)
                msgr.Send(toId, fe.Host, fe.Port, text)
                RememberEndpoint(toId, cand.Endpoint)   ' 次回からは、まずこのアドレスに送る
                Return $"ホスト {cand.Host} で相手を発見し、{cand.Endpoint} へ送信"
            Catch ex As Exception
                lastError = ex.Message
                RaiseEvent Info($"ホスト {cand.Host} が示した {cand.Endpoint} には送れませんでした({ex.Message})。他のホストの情報を探します。")
            End Try
        Next

        If foundAny Then Throw New IOException($"ホストが示したアドレスへ送信できませんでした: {lastError}")
        Throw New IOException("ホストに相手が登録されていません(オフラインの可能性)。" & If(lastError IsNot Nothing, $" 登録アドレスへの送信も失敗: {lastError}", ""))
    End Function

    ' ---- ホストで見つけた相手のアドレスのキャッシュ(メモリのみ。アプリを閉じると消える) ----

    Private Class CacheEntry
        Public Endpoint As String = ""
        Public Stamp As DateTime
    End Class

    Private ReadOnly _addrCache As New Dictionary(Of String, CacheEntry)
    Private ReadOnly _cacheLock As New Object()
    Private Const MaxCache As Integer = 2000

    ''' <summary>覚えているアドレス。なければ、または、覚えてから AddressCacheMinutes 分を過ぎていれば Nothing</summary>
    Private Function CachedEndpoint(id As String) As String
        Dim minutes = _peers.AddressCacheMinutes
        If minutes <= 0 Then Return Nothing
        SyncLock _cacheLock
            Dim e As CacheEntry = Nothing
            If Not _addrCache.TryGetValue(id, e) Then Return Nothing
            If DateTime.UtcNow - e.Stamp > TimeSpan.FromMinutes(minutes) Then
                _addrCache.Remove(id)
                Return Nothing
            End If
            Return e.Endpoint
        End SyncLock
    End Function

    Private Sub RememberEndpoint(id As String, endpoint As String)
        If _peers.AddressCacheMinutes <= 0 Then Return
        SyncLock _cacheLock
            If Not _addrCache.ContainsKey(id) AndAlso _addrCache.Count >= MaxCache Then
                _addrCache.Remove(_addrCache.OrderBy(Function(kv) kv.Value.Stamp).First().Key)     ' 満杯なら、最も古いものを捨てる
            End If
            _addrCache(id) = New CacheEntry() With {.Endpoint = endpoint, .Stamp = DateTime.UtcNow}
        End SyncLock
    End Sub

    Private Sub ForgetEndpoint(id As String)
        SyncLock _cacheLock
            _addrCache.Remove(id)
        End SyncLock
    End Sub

    ''' <summary>
    ''' 連絡先 peerId から、自分宛の予約メッセージを受け取る。接続は、登録アドレス → 前回ホストで見つけたアドレス(キャッシュ)
    ''' → ホストでの検索 の順に試す。受け取った件数を返す(どのアドレスにもつながらなければ -1)。
    ''' </summary>
    Public Function PullFrom(msgr As Messenger, peerId As String, manualEndpoint As String) As Integer
        Dim tried As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        If Not String.IsNullOrWhiteSpace(manualEndpoint) Then
            Try
                Dim ep = ParseEndpoint(manualEndpoint)
                tried.Add($"{ep.Host}:{ep.Port}")
                Return msgr.Pull(peerId, ep.Host, ep.Port)
            Catch ex As Exception
                ' 登録アドレスにつながらない(または相手が違う) → 次を試す
            End Try
        End If

        Dim cached = CachedEndpoint(peerId)
        If cached IsNot Nothing AndAlso Not tried.Contains(cached) Then
            tried.Add(cached)
            Try
                Dim ce = ParseEndpoint(cached)
                Dim n = msgr.Pull(peerId, ce.Host, ce.Port)
                RememberEndpoint(peerId, cached)
                Return n
            Catch ex As Exception
                ForgetEndpoint(peerId)
            End Try
        End If

        If _peers.HostsSnapshot().Count = 0 Then Return -1
        For Each cand In Candidates(peerId, True)
            If tried.Contains(cand.Endpoint) Then Continue For
            tried.Add(cand.Endpoint)
            Try
                Dim ep = ParseEndpoint(cand.Endpoint)
                Dim n = msgr.Pull(peerId, ep.Host, ep.Port)
                RememberEndpoint(peerId, cand.Endpoint)
                Return n
            Catch ex As Exception
                ' 次の候補を試す
            End Try
        Next
        Return -1
    End Function

    ''' <summary>
    ''' 連絡先の全員に、自分宛の予約メッセージを問い合わせる(QueryParallel 人ずつ並行)。
    ''' 1人ごとの結果(受け取った件数。つながらなければ -1)を onResult で知らせる。
    ''' </summary>
    Public Sub PollContacts(msgr As Messenger, contacts As List(Of Contact), onResult As Action(Of Contact, Integer))
        Dim idx = 0
        While idx < contacts.Count
            Dim n = Math.Min(_peers.QueryParallel, contacts.Count - idx)
            Dim batch = contacts.GetRange(idx, n)
            idx += n
            Dim tasks = batch.Select(Function(c) Task.Run(Sub() PollOne(msgr, c, onResult))).ToArray()
            Task.WaitAll(tasks)
        End While
    End Sub

    Private Sub PollOne(msgr As Messenger, c As Contact, onResult As Action(Of Contact, Integer))
        Dim got = -1
        Try
            got = PullFrom(msgr, c.Id, c.Endpoint)
        Catch ex As Exception
            got = -1
        End Try
        onResult(c, got)
    End Sub

    ''' <summary>同じ状態のログを繰り返し出さない</summary>
    Private Sub ReportStatusChange(message As String)
        If message = _lastStatus Then Return
        _lastStatus = message
        RaiseEvent Info(message)
    End Sub

End Class
