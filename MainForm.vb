Option Strict On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Threading.Tasks
Imports System.Windows.Forms

''' <summary>メールソフト風のメイン画面: フォルダ → メッセージ一覧 → 内容表示。メッセージの削除・移動、フォルダの作成・変更・削除ができる</summary>
Public Class MainForm
    Inherits Form

    Private ReadOnly _userPath As String
    Private ReadOnly _contactsPath As String
    Private ReadOnly _inboxPath As String
    Private ReadOnly _sentPath As String
    Private ReadOnly _foldersPath As String
    Private ReadOnly _rulesPath As String
    Private ReadOnly _msgr As Messenger
    Private _logLines As Integer                               ' ログに表示している行数
    Private _restoring As Boolean                              ' 起動時の状態復元中(ホストへの通知を1回にまとめる)

    ' ホスト機能 (メッセージ送受信とは別ポート・別処理)
    Private ReadOnly _peers As PeerList
    Private ReadOnly _hostService As HostService
    Private ReadOnly _hostClient As HostClient
    Private ReadOnly _announceTimer As System.Threading.Timer
    Private _reservations As ReservationStore                  ' 送信予約 (Outbox.dat)
    Private ReadOnly tmrPoll As New System.Windows.Forms.Timer()
    Private _polling As Boolean                                ' 予約メッセージの問い合わせを実行中か
    Private ReadOnly _pollStatus As New Dictionary(Of String, Integer)   ' 連絡先IDごとの前回結果(-1=つながらない)
    Private ReadOnly _pollLock As New Object()
    Private _msgPortAnnounce As Integer
    Private _hostPortAnnounce As Integer

    ' メッセージとフォルダ
    Private ReadOnly _contacts As New List(Of Contact)
    Private ReadOnly _inbox As New List(Of MailItem)           ' Messages.dat (受信)
    Private ReadOnly _sent As New List(Of MailItem)            ' Sent.dat (送信)
    Private ReadOnly _unread As New HashSet(Of MailItem)       ' 今回の起動中に受信して、まだ開いていないもの
    Private ReadOnly _rules As New List(Of MailRule)           ' 振り分けルール (Rules.dat)
    Private ReadOnly _folders As New List(Of FolderItem)       ' ユーザーが作成したフォルダ (Folders.dat)
    Private ReadOnly _fInbox As New FolderItem() With {.Kind = FolderKind.Inbox, .Name = "受信トレイ"}
    Private ReadOnly _fSent As New FolderItem() With {.Kind = FolderKind.Sent, .Name = "送信済み"}
    Private ReadOnly _fOutbox As New FolderItem() With {.Kind = FolderKind.Outbox, .Name = "送信予約"}
    Private ReadOnly _fTrash As New FolderItem() With {.Kind = FolderKind.Trash, .Id = FolderStore.TrashId, .Name = "ごみ箱"}
    Private ReadOnly _fLog As New FolderItem() With {.Kind = FolderKind.Log, .Name = "ログ"}
    Private ReadOnly _fDefault As New FolderItem() With {.Kind = FolderKind.Inbox, .Name = "標準のフォルダ(受信→受信トレイ / 送信→送信済み)"}   ' 移動メニュー専用
    Private _current As FolderItem                             ' 選択中のフォルダ
    Private _dragging As List(Of MailItem)                     ' ドラッグ中のメッセージ(複数選択中ならすべて)
    Private ReadOnly _boldFont As Font

    ' ツールバー・ステータスバー
    Private ReadOnly tsMain As New ToolStrip() With {.GripStyle = ToolStripGripStyle.Hidden}
    Private ReadOnly btnNew As New ToolStripButton("新規メッセージ")
    Private ReadOnly btnReply As New ToolStripButton("返信") With {.Enabled = False}
    Private ReadOnly btnVerify As New ToolStripButton("署名を検証") With {.Enabled = False}
    Private ReadOnly btnMove As New ToolStripDropDownButton("移動") With {.Enabled = False}
    Private ReadOnly btnDelete As New ToolStripButton("削除") With {.Enabled = False}
    Private ReadOnly btnEmptyTrash As New ToolStripButton("ごみ箱を空にする") With {.Visible = False}
    Private ReadOnly btnFolder As New ToolStripDropDownButton("フォルダ")
    Private ReadOnly btnRules As New ToolStripButton("振り分け") With {.ToolTipText = "受信したメッセージを、条件に応じて自動でフォルダへ移動するルール"}
    Private ReadOnly btnPollNow As New ToolStripButton("予約を確認") With {.ToolTipText = "連絡先に、自分宛の予約メッセージを今すぐ問い合わせます"}
    Private ReadOnly btnContacts As New ToolStripButton("連絡先")
    Private ReadOnly btnSettings As New ToolStripButton("接続設定") With {.CheckOnClick = True, .Checked = False}     ' 起動時は非表示(押すと表示)
    Private ReadOnly stsMain As New StatusStrip()
    Private ReadOnly lblListen As New ToolStripStatusLabel("受信: 停止中")
    Private ReadOnly lblHostState As New ToolStripStatusLabel("ホスト: 停止中")
    Private ReadOnly lblUnread As New ToolStripStatusLabel() With {.Spring = True, .TextAlign = ContentAlignment.MiddleRight}

    ' 接続設定パネル
    Private ReadOnly txtName As New TextBox() With {.Width = 160, .MaxLength = 40}
    Private ReadOnly nudPort As New NumericUpDown() With {.Minimum = 1, .Maximum = 65535, .Value = 50000, .Width = 80}
    Private ReadOnly chkListen As New CheckBox() With {.Text = "メッセージを受信する", .AutoSize = False, .Width = 200, .Height = 26}
    Private ReadOnly txtMyId As New TextBox() With {.ReadOnly = True, .Width = 560}
    Private ReadOnly btnCopy As New Button() With {.Text = "コピー", .AutoSize = True}
    Private ReadOnly chkHost As New CheckBox() With {.Text = "ホスト機能を提供する", .AutoSize = False, .Width = 200, .Height = 26}
    Private ReadOnly chkPoll As New CheckBox() With {.Text = "予約メッセージを問い合わせる", .AutoSize = False, .Width = 240, .Height = 26}
    Private ReadOnly lblPollInfo As New Label() With {.AutoSize = True, .Margin = New Padding(3, 6, 3, 3)}      ' 長さに合わせて大きさが決まる(切れない)
    Private ReadOnly nudHostPort As New NumericUpDown() With {.Minimum = 1, .Maximum = 65535, .Value = 50001, .Width = 80}
    Private ReadOnly pnlSettings As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(6), .Visible = False}

    ' メール表示
    Private ReadOnly splitMain As New SplitContainer() With {.Dock = DockStyle.Fill, .FixedPanel = FixedPanel.Panel1}
    Private ReadOnly splitMail As New SplitContainer() With {.Dock = DockStyle.Fill, .Orientation = Orientation.Horizontal}
    Private ReadOnly lstFolders As New ListBox() With {.Dock = DockStyle.Fill, .IntegralHeight = False, .BorderStyle = BorderStyle.None,
        .ItemHeight = 28, .AllowDrop = True}
    Private ReadOnly lvMessages As New ListView() With {.Dock = DockStyle.Fill, .View = View.Details, .FullRowSelect = True,
        .MultiSelect = True, .HideSelection = False}
    Private ReadOnly ctxMessages As New ContextMenuStrip()
    Private ReadOnly ctxFolders As New ContextMenuStrip()
    Private ReadOnly lblHeader As New Label() With {.Dock = DockStyle.Top, .Height = 50, .AutoSize = False, .Padding = New Padding(6)}
    Private ReadOnly lblVerify As New Label() With {.Dock = DockStyle.Top, .Height = 28, .AutoSize = False, .Padding = New Padding(6, 0, 6, 0), .TextAlign = ContentAlignment.MiddleLeft}
    Private ReadOnly txtBody As New TextBox() With {.Multiline = True, .ReadOnly = True, .ScrollBars = ScrollBars.Vertical, .Dock = DockStyle.Fill}
    Private ReadOnly txtLog As New TextBox() With {.Multiline = True, .ReadOnly = True, .ScrollBars = ScrollBars.Vertical, .Dock = DockStyle.Fill, .Visible = False}

    Public Sub New()
        Dim resources As New System.ComponentModel.ComponentResourceManager(GetType(MainForm))
        Me.Icon = DirectCast(resources.GetObject("Icon1"), System.Drawing.Icon)
        Text = "PeerMail"
        Font = New Font("Yu Gothic UI", 10.0F)
        ClientSize = New Size(1000, 680)
        MinimumSize = New Size(800, 500)
        _boldFont = New Font(Font, FontStyle.Bold)

        ' データは実行フォルダに保存する。第1引数でフォルダを指定すれば、同じexeを別データで複数起動できる
        Dim dataDir = AppContext.BaseDirectory
        Dim args = Environment.GetCommandLineArgs()
        If args.Length > 1 Then dataDir = Path.GetFullPath(args(1))
        Directory.CreateDirectory(dataDir)
        Text = "P2P Messenger - " & dataDir
        _userPath = Path.Combine(dataDir, "User.dat")
        _contactsPath = Path.Combine(dataDir, "Contacts.dat")
        _inboxPath = Path.Combine(dataDir, "Messages.dat")
        _sentPath = Path.Combine(dataDir, "Sent.dat")
        _foldersPath = Path.Combine(dataDir, "Folders.dat")
        _rulesPath = Path.Combine(dataDir, "Rules.dat")

        BuildLayout()

        Dim ident = Identity.LoadOrCreate(_userPath)
        _msgr = New Messenger(ident, Path.Combine(dataDir, "Session.dat"))
        AddHandler _msgr.MessageReceived, AddressOf OnMessageReceived
        AddHandler _msgr.Info, AddressOf Log

        _peers = PeerList.Load(Path.Combine(dataDir, "Peers.ini"))
        _hostClient = New HostClient(ident, _peers)
        _hostService = New HostService(_peers)
        AddHandler _hostClient.Info, AddressOf Log
        AddHandler _hostService.Info, AddressOf Log
        _announceTimer = New System.Threading.Timer(AddressOf OnAnnounceTick, Nothing,
                                                    System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite)

        ' 送信予約: 相手が問い合わせてきたときに渡す(渡す側は、受信待機中である必要がある)
        _reservations = New ReservationStore(Path.Combine(dataDir, "Outbox.dat"))
        _msgr.Reservations = _reservations
        _msgr.PullBatch = _peers.PullBatch
        AddHandler _msgr.ReservedDelivered, AddressOf OnReservedDelivered
        AddHandler tmrPoll.Tick, AddressOf OnPollTick

        txtName.Text = ident.DisplayName
        RefreshMyId()
        LoadContacts()
        _folders.AddRange(FolderStore.Load(_foldersPath))
        _rules.AddRange(RuleStore.Load(_rulesPath))
        UpdatePollInfo()
        _inbox.AddRange(MailStore.Load(_inboxPath, False))
        _sent.AddRange(MailStore.Load(_sentPath, True))
        ' 存在しないフォルダを指しているメッセージは、標準の場所(受信トレイ/送信済み)に戻す
        For Each m In _inbox.Concat(_sent)
            If m.Folder.Length > 0 AndAlso m.Folder <> FolderStore.TrashId AndAlso Not _folders.Any(Function(f) f.Id = m.Folder) Then m.Folder = ""
        Next
        Dim expired = _reservations.ExpireOlderThan(_peers.ReservationExpireDays)
        If expired > 0 Then Log($"保管期間({_peers.ReservationExpireDays}日)を過ぎた予約を {expired} 件削除しました。")
        RebuildFolderList(_fInbox)
        UpdateStatus()
        Log("起動しました。自分の公開IDを相手に伝え、相手の公開IDを連絡先に登録してください(IP:ポートが空欄ならホストで検索します)。")
    End Sub

    Protected Overrides Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)

        ' 分割位置は画面サイズが確定してから設定する
        Try
            splitMain.SplitterDistance = 170
            splitMail.SplitterDistance = Math.Max(120, splitMail.Height * 2 \ 5)
        Catch ex As Exception
        End Try

        ' 前回のポート番号を復元してから、変更時の保存を有効にする
        nudPort.Value = _peers.ListenPort
        nudHostPort.Value = _peers.HostPort
        AddHandler nudPort.ValueChanged, AddressOf OnPortChanged
        AddHandler nudHostPort.ValueChanged, AddressOf OnPortChanged

        ' 前回の状態(メッセージ受信・ホスト機能の提供)を復元する。ホストへの通知は最後に1回だけ行う
        _restoring = True
        If _peers.ListenEnabled Then chkListen.Checked = True  ' → OnListenToggled が受信を開始する
        If _peers.HostEnabled Then chkHost.Checked = True      ' → OnHostToggled がホストを開始する
        If _peers.PollEnabled Then chkPoll.Checked = True      ' → OnPollToggled が予約メッセージの問い合わせを再開する
        _restoring = False
        ApplyState()
        Dim t = Task.Run(Sub() _hostClient.RefreshHosts())     ' 起動時にホストから他ホストの情報を受け取る
    End Sub

    ' ====================== レイアウト ======================

    Private Sub BuildLayout()
        ' ツールバー
        tsMain.Items.AddRange(New ToolStripItem() {btnNew, btnReply, btnVerify, New ToolStripSeparator(), btnMove, btnDelete, btnEmptyTrash,
                                                   New ToolStripSeparator(), btnFolder, btnRules, New ToolStripSeparator(), btnPollNow, btnContacts, btnSettings})

        ' ドロップダウン/右クリックメニューは、開く直前に中身を作り直す。項目が空だと開かないので、仮の項目を入れておく
        btnMove.DropDownItems.Add(New ToolStripMenuItem("(メッセージを選択してください)") With {.Enabled = False})
        btnFolder.DropDownItems.Add(New ToolStripMenuItem("(読み込み中)") With {.Enabled = False})
        ctxMessages.Items.Add(New ToolStripMenuItem("(読み込み中)") With {.Enabled = False})
        ctxFolders.Items.Add(New ToolStripMenuItem("(読み込み中)") With {.Enabled = False})

        ' ステータスバー
        stsMain.Items.AddRange(New ToolStripItem() {lblListen, New ToolStripStatusLabel("|"), lblHostState, lblUnread})

        ' 接続設定パネル (ツールバーの「接続設定」で表示/非表示)
        pnlSettings.Controls.AddRange(New Control() {Lbl("表示名"), txtName})
        pnlSettings.SetFlowBreak(txtName, True)
        pnlSettings.Controls.AddRange(New Control() {Lbl("自分の公開ID"), txtMyId, btnCopy})
        pnlSettings.SetFlowBreak(btnCopy, True)
        ' 受信とホスト機能は、どちらも「チェック + ポート番号」で操作する
        pnlSettings.Controls.AddRange(New Control() {Lbl(""), chkListen, Lbl("受信ポート"), nudPort})
        pnlSettings.SetFlowBreak(nudPort, True)
        pnlSettings.Controls.AddRange(New Control() {Lbl(""), chkHost, Lbl("ホストポート"), nudHostPort})
        pnlSettings.SetFlowBreak(nudHostPort, True)
        pnlSettings.Controls.AddRange(New Control() {Lbl(""), chkPoll, lblPollInfo})

        ' メッセージ一覧 + 内容表示
        lvMessages.Columns.Add("日時", 150)
        lvMessages.Columns.Add("差出人", 420)
        lvMessages.Columns.Add("本文", 500)
        lvMessages.ContextMenuStrip = ctxMessages
        splitMail.Panel1.Controls.Add(lvMessages)
        splitMail.Panel2.Controls.Add(txtBody)         ' Fill は最初に追加する
        splitMail.Panel2.Controls.Add(lblVerify)
        splitMail.Panel2.Controls.Add(lblHeader)

        ' フォルダ一覧 | (メール表示 / ログ)
        lstFolders.ContextMenuStrip = ctxFolders
        splitMain.Panel1.Controls.Add(lstFolders)
        splitMain.Panel2.Controls.Add(splitMail)
        splitMain.Panel2.Controls.Add(txtLog)

        ' Fill を最初に、画面端に付くものは後から追加する
        Controls.Add(splitMain)
        Controls.Add(stsMain)
        Controls.Add(pnlSettings)
        Controls.Add(tsMain)

        AddHandler chkListen.CheckedChanged, AddressOf OnListenToggled
        AddHandler chkHost.CheckedChanged, AddressOf OnHostToggled
        AddHandler chkPoll.CheckedChanged, AddressOf OnPollToggled
        AddHandler btnPollNow.Click, Sub(s, e) StartPoll(True)
        AddHandler btnRules.Click, Sub(s, e) OpenRules()
        AddHandler btnCopy.Click, Sub(s, e) Clipboard.SetText(txtMyId.Text)
        AddHandler txtName.TextChanged, AddressOf OnNameChanged
        AddHandler txtName.Leave, Sub(s, e) _msgr.Self.Save(_userPath)
        AddHandler btnNew.Click, Sub(s, e) OpenCompose(Nothing)
        AddHandler btnReply.Click, AddressOf OnReply
        AddHandler btnVerify.Click, AddressOf OnVerify
        AddHandler btnDelete.Click, AddressOf OnDeleteMessage
        AddHandler btnEmptyTrash.Click, AddressOf OnEmptyTrash
        AddHandler btnMove.DropDownOpening, AddressOf OnMoveMenuOpening
        AddHandler btnFolder.DropDownOpening, AddressOf OnFolderMenuOpening
        AddHandler btnContacts.Click, Sub(s, e) OpenContacts("")
        AddHandler btnSettings.CheckedChanged, Sub(s, e) pnlSettings.Visible = btnSettings.Checked
        AddHandler lstFolders.SelectedIndexChanged, Sub(s, e) ShowFolder()
        AddHandler lstFolders.MouseDown, AddressOf OnFolderMouseDown
        AddHandler lstFolders.DragOver, AddressOf OnFolderDragOver
        AddHandler lstFolders.DragDrop, AddressOf OnFolderDragDrop
        AddHandler ctxFolders.Opening, AddressOf OnFolderContextOpening
        AddHandler lvMessages.SelectedIndexChanged, AddressOf OnMessageSelected
        AddHandler lvMessages.ItemDrag, AddressOf OnMessageDrag
        AddHandler lvMessages.KeyDown, AddressOf OnMessageKeyDown
        AddHandler lvMessages.Resize, Sub(s, e) FitColumns()
        AddHandler ctxMessages.Opening, AddressOf OnMessageContextOpening
    End Sub

    Private Shared Function Lbl(t As String) As Label
        Return New Label() With {.Text = t, .AutoSize = False, .Width = 100, .Height = 26, .TextAlign = ContentAlignment.MiddleLeft}
    End Function

    Private Sub FitColumns()
        If lvMessages.Columns.Count < 3 Then Return
        lvMessages.Columns(2).Width = Math.Max(150, lvMessages.ClientSize.Width - lvMessages.Columns(0).Width - lvMessages.Columns(1).Width - 4)
    End Sub

    ' ====================== フォルダ・一覧・内容表示 ======================

    ''' <summary>フォルダ一覧を作り直し、target を選択する(受信トレイ → 送信済み → ユーザーフォルダ → ログ)</summary>
    Private Sub RebuildFolderList(target As FolderItem)
        lstFolders.BeginUpdate()
        lstFolders.Items.Clear()
        lstFolders.Items.Add(_fInbox)
        lstFolders.Items.Add(_fSent)
        lstFolders.Items.Add(_fOutbox)
        For Each f In _folders
            lstFolders.Items.Add(f)
        Next
        lstFolders.Items.Add(_fTrash)
        lstFolders.Items.Add(_fLog)
        lstFolders.EndUpdate()
        lstFolders.SelectedItem = target
    End Sub

    Private Sub ShowFolder()
        Dim f = TryCast(lstFolders.SelectedItem, FolderItem)
        If f Is Nothing Then Return
        _current = f
        Dim isLog = (f.Kind = FolderKind.Log)
        btnEmptyTrash.Visible = (f.Kind = FolderKind.Trash)
        splitMail.Visible = Not isLog
        txtLog.Visible = isLog
        If isLog Then
            ShowMessage(Nothing)
            Return
        End If
        lvMessages.Columns(1).Text = If(f.Kind = FolderKind.Inbox, "差出人", If(f.Kind = FolderKind.Sent OrElse f.Kind = FolderKind.Outbox, "宛先", "相手"))
        FillList()
    End Sub

    ''' <summary>選んでいるフォルダに入っているメッセージ</summary>
    Private Function CurrentItems() As List(Of MailItem)
        Select Case _current.Kind
            Case FolderKind.Inbox
                Return _inbox.Where(Function(m) m.Folder.Length = 0).ToList()
            Case FolderKind.Sent
                Return _sent.Where(Function(m) m.Folder.Length = 0).ToList()
            Case FolderKind.Outbox
                Return _reservations.Snapshot()
            Case FolderKind.User, FolderKind.Trash
                Dim id = _current.Id
                Return _inbox.Concat(_sent).Where(Function(m) m.Folder = id).OrderBy(Function(m) m.Time).ToList()
            Case Else
                Return New List(Of MailItem)()
        End Select
    End Function

    ''' <summary>選んでいるフォルダのメッセージを新しい順に表示する</summary>
    Private Sub FillList()
        If _current Is Nothing OrElse _current.Kind = FolderKind.Log Then Return
        Dim src = CurrentItems()
        Dim rows As New List(Of ListViewItem)(src.Count)
        For i = src.Count - 1 To 0 Step -1
            rows.Add(MakeRow(src(i)))
        Next
        lvMessages.BeginUpdate()
        lvMessages.Items.Clear()
        lvMessages.Items.AddRange(rows.ToArray())
        lvMessages.EndUpdate()
        ShowMessage(Nothing)
    End Sub

    Private Function MakeRow(m As MailItem) As ListViewItem
        Dim who = WhoLabel(m)
        ' ユーザーフォルダには受信と送信が混ざるので、向きを付ける
        If _current IsNot Nothing AndAlso (_current.Kind = FolderKind.User OrElse _current.Kind = FolderKind.Trash) Then who = If(m.IsSent, "→ ", "← ") & who
        Dim it As New ListViewItem(New String() {m.Time.ToString("yyyy-MM-dd HH:mm"), who, OneLine(m.Text)})
        it.Tag = m
        If _unread.Contains(m) Then it.Font = _boldFont
        Return it
    End Function

    Private Shared Function OneLine(s As String) As String
        Dim t = s.Replace(vbCr, " ").Replace(vbLf, " ").Replace(vbTab, " ")
        Return If(t.Length > 100, t.Substring(0, 100) & "…", t)
    End Function

    ''' <summary>差出人・宛先の表示。連絡先に登録済みならその表示名、未登録なら相手が名乗っている表示名を使い、
    ''' 常に「表示名@実効ID」の形で表す(実効IDが同じ相手かどうかの判断材料になる)</summary>
    Private Function WhoLabel(m As MailItem) As String
        Return WhoLabel(m.PeerId, m.ClaimedName)
    End Function

    Private Function WhoLabel(id As String, claimedName As String) As String
        Dim c = _contacts.FirstOrDefault(Function(x) x.Id = id)
        If c IsNot Nothing Then Return $"{c.Name}@{id}"
        Dim name = If(String.IsNullOrEmpty(claimedName), "(名前不明)", claimedName)
        Return $"{name}@{id} (未登録)"
    End Function

    ''' <summary>選択中のメッセージがちょうど1通のとき、そのメッセージ(複数選択中・未選択のときは Nothing)</summary>
    Private Function SelectedMessage() As MailItem
        If lvMessages.SelectedItems.Count <> 1 Then Return Nothing
        Return DirectCast(lvMessages.SelectedItems(0).Tag, MailItem)
    End Function

    ''' <summary>選択中のすべてのメッセージ</summary>
    Private Function SelectedMessages() As List(Of MailItem)
        Return lvMessages.SelectedItems.Cast(Of ListViewItem)().Select(Function(it) DirectCast(it.Tag, MailItem)).ToList()
    End Function

    Private Sub OnMessageSelected(sender As Object, e As EventArgs)
        Dim n = lvMessages.SelectedItems.Count
        If n = 0 Then
            ShowMessage(Nothing)
            Return
        End If
        If n > 1 Then
            ShowMultiple(n)
            Return
        End If
        Dim it = lvMessages.SelectedItems(0)
        Dim m = DirectCast(it.Tag, MailItem)
        ShowMessage(m)
        If _unread.Remove(m) Then
            it.Font = lvMessages.Font
            UpdateStatus()
        End If
    End Sub

    ''' <summary>複数選択中: 内容は表示せず、移動と削除だけできる</summary>
    Private Sub ShowMultiple(n As Integer)
        lblHeader.Text = $"{n} 件のメッセージを選択中です。移動・削除ができます。"
        lblVerify.Text = ""
        txtBody.Clear()
        btnReply.Enabled = False
        btnVerify.Enabled = False
        Dim all = SelectedMessages()
        btnMove.Enabled = Not all.Any(Function(m) m.IsReserved)
        btnDelete.Enabled = True
        btnDelete.Text = If(all.All(Function(m) m.IsReserved), "予約を取り消す", If(all.All(Function(m) m.Folder = FolderStore.TrashId), "完全に削除", "削除"))
    End Sub

    Private Sub ShowMessage(m As MailItem)
        If m Is Nothing Then
            lblHeader.Text = ""
            lblVerify.Text = ""
            txtBody.Clear()
            btnReply.Enabled = False
            btnVerify.Enabled = False
            btnMove.Enabled = False
            btnDelete.Enabled = False
            btnDelete.Text = "削除"
            Return
        End If
        Dim label = If(m.IsSent, "宛先", "差出人")
        ' 連絡先の表示名と、相手が名乗っている表示名が違うときは、名乗りも併記する
        Dim claimedNote = ""
        If Not m.IsSent AndAlso m.ClaimedName.Length > 0 Then
            Dim contact = _contacts.FirstOrDefault(Function(x) x.Id = m.PeerId)
            If contact IsNot Nothing AndAlso contact.Name <> m.ClaimedName Then claimedNote = $"  (相手の自称: {m.ClaimedName})"
        End If
        If m.IsReserved Then
            lblHeader.Text = $"{label}: {WhoLabel(m)}  [予約中: 相手が問い合わせてきたときに送信します]{vbCrLf}予約した日時: {m.Time:yyyy-MM-dd HH:mm:ss}"
        Else
            lblHeader.Text = $"{label}: {WhoLabel(m)}{claimedNote}{vbCrLf}日時: {m.Time:yyyy-MM-dd HH:mm:ss}"
        End If
        txtBody.Text = m.Text.Replace(vbCrLf, vbLf).Replace(vbLf, vbCrLf)
        btnReply.Enabled = Not m.IsSent
        btnVerify.Enabled = Not m.IsSent
        btnMove.Enabled = Not m.IsReserved
        btnDelete.Enabled = True
        btnDelete.Text = If(m.IsReserved, "予約を取り消す", If(m.Folder = FolderStore.TrashId, "完全に削除", "削除"))
        UpdateVerifyLabel(m)
    End Sub

    ''' <summary>受信メッセージを選んだとき、保存されている署名の証拠で自動的に検証して結果を表示する</summary>
    Private Sub UpdateVerifyLabel(m As MailItem)
        If m.IsSent Then
            lblVerify.Text = ""
            Return
        End If
        If m.Evidence Is Nothing Then
            lblVerify.ForeColor = SystemColors.GrayText
            lblVerify.Text = "署名の記録: なし(署名の記録を始める前に受信したメッセージ)"
            Return
        End If
        If m.Evidence.Verify(m.PeerId, _msgr.Self.Id, m.Text).AllOk Then
            lblVerify.ForeColor = Color.SeaGreen
            lblVerify.Text = "署名の検証: 正常(署名・本文とも改ざんなし)"
        Else
            lblVerify.ForeColor = Color.Firebrick
            lblVerify.Text = "署名の検証: 異常あり。改ざんされている可能性があります(「署名を検証」で詳細を表示)"
        End If
    End Sub

    ''' <summary>「署名を検証」: 検証の各項目の結果を表示する</summary>
    Private Sub OnVerify(sender As Object, e As EventArgs)
        Dim m = SelectedMessage()
        If m Is Nothing OrElse m.IsSent Then Return
        If m.Evidence Is Nothing Then
            MessageBox.Show(Me, "このメッセージには署名の記録がありません。" & vbCrLf &
                            "(署名の記録を始める前に受信したメッセージは検証できません)", "署名の検証", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        Dim rep = m.Evidence.Verify(m.PeerId, _msgr.Self.Id, m.Text)
        Dim head = If(rep.AllOk,
                      "すべての検証に成功しました。この実効IDの秘密鍵を持つ人が、この内容を送信したことを確認できます。",
                      "検証に失敗した項目があります。保存されている本文または署名の記録が、受信後に変更された可能性があります。")
        MessageBox.Show(Me, $"差出人: {WhoLabel(m)}{vbCrLf}{vbCrLf}{head}{vbCrLf}{vbCrLf}{rep.ToText()}",
                        "署名の検証", MessageBoxButtons.OK, If(rep.AllOk, MessageBoxIcon.Information, MessageBoxIcon.Warning))
    End Sub

    ' ====================== メッセージの削除・移動 ======================

    Private Sub SaveMailFile(isSent As Boolean)
        Try
            If isSent Then
                MailStore.Save(_sentPath, _sent)
            Else
                MailStore.Save(_inboxPath, _inbox)
            End If
        Catch ex As Exception
            Log("メッセージファイルを更新できませんでした: " & ex.Message)
            MessageBox.Show(Me, "メッセージファイルを更新できませんでした: " & ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
    End Sub

    Private Sub SaveFolders()
        Try
            FolderStore.Save(_foldersPath, _folders)
        Catch ex As Exception
            Log("フォルダ一覧を保存できませんでした: " & ex.Message)
            MessageBox.Show(Me, "フォルダ一覧を保存できませんでした: " & ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
    End Sub

    ''' <summary>
    ''' 「削除」(選択中のすべてのメッセージが対象): ごみ箱の外にあるものは、警告なしでごみ箱へ移動する。
    ''' ごみ箱の中にあるものは、警告を出したうえで完全に削除する(署名の記録も一緒に消える)。
    ''' </summary>
    Private Sub OnDeleteMessage(sender As Object, e As EventArgs)
        Dim ms = SelectedMessages()
        If ms.Count = 0 Then Return
        ' 送信予約は、ごみ箱を通さず、確認のうえ取り消す
        Dim reservedItems = ms.Where(Function(m) m.IsReserved).ToList()
        If reservedItems.Count > 0 Then
            CancelReservations(reservedItems)
            Return
        End If
        Dim toTrash = ms.Where(Function(m) m.Folder <> FolderStore.TrashId).ToList()
        Dim forever = ms.Where(Function(m) m.Folder = FolderStore.TrashId).ToList()
        If toTrash.Count > 0 Then MoveToTrash(toTrash)
        If forever.Count = 0 Then Return

        Dim what = If(forever.Count = 1, "このメッセージ", $"選択した {forever.Count} 件のメッセージ")
        Dim msg = $"{what}を完全に削除します。" & vbCrLf & "本文と署名の記録が失われ、元に戻せません。" & vbCrLf & vbCrLf & "削除しますか?"
        If MessageBox.Show(Me, msg, "完全に削除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then Return
        RemoveForever(forever)
        Log($"{forever.Count} 件のメッセージを完全に削除しました。")
    End Sub

    Private Sub MoveToTrash(items As List(Of MailItem))
        For Each m In items
            m.Folder = FolderStore.TrashId
            _unread.Remove(m)
        Next
        SaveMailFiles(items)
        FillList()
        UpdateStatus()
        Log($"{items.Count} 件のメッセージをごみ箱へ移動しました。")
    End Sub

    ''' <summary>items に含まれる受信・送信それぞれのファイルを保存し直す</summary>
    Private Sub SaveMailFiles(items As List(Of MailItem))
        If items.Any(Function(m) Not m.IsSent) Then SaveMailFile(False)
        If items.Any(Function(m) m.IsSent) Then SaveMailFile(True)
    End Sub

    ''' <summary>メッセージを一覧とファイルから完全に取り除く</summary>
    Private Sub RemoveForever(items As List(Of MailItem))
        For Each m In items
            If m.IsSent Then
                _sent.Remove(m)
            Else
                _inbox.Remove(m)
            End If
            _unread.Remove(m)
        Next
        If items.Any(Function(m) Not m.IsSent) Then SaveMailFile(False)
        If items.Any(Function(m) m.IsSent) Then SaveMailFile(True)
        FillList()
        UpdateStatus()
    End Sub

    ''' <summary>「ごみ箱を空にする」: ごみ箱のメッセージをすべて完全に削除する(警告あり)</summary>
    Private Sub OnEmptyTrash(sender As Object, e As EventArgs)
        Dim doomed = _inbox.Concat(_sent).Where(Function(m) m.Folder = FolderStore.TrashId).ToList()
        If doomed.Count = 0 Then
            MessageBox.Show(Me, "ごみ箱は空です。", "ごみ箱を空にする", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        Dim msg = $"ごみ箱の {doomed.Count} 件のメッセージをすべて完全に削除します。" & vbCrLf &
                  "本文と署名の記録が失われ、元に戻せません。" & vbCrLf & vbCrLf & "削除しますか?"
        If MessageBox.Show(Me, msg, "ごみ箱を空にする", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then Return
        RemoveForever(doomed)
        Log($"ごみ箱を空にしました({doomed.Count} 件を完全に削除)。")
    End Sub

    Private Sub OnMessageKeyDown(sender As Object, e As KeyEventArgs)
        If e.KeyCode = Keys.Delete Then
            e.Handled = True
            OnDeleteMessage(Nothing, EventArgs.Empty)
        ElseIf e.Control AndAlso e.KeyCode = Keys.A Then
            ' Ctrl+A: 表示中のメッセージをすべて選択
            e.Handled = True
            lvMessages.BeginUpdate()
            For Each it As ListViewItem In lvMessages.Items
                it.Selected = True
            Next
            lvMessages.EndUpdate()
        End If
    End Sub

    ''' <summary>メッセージ m を folder へ移動できるか(受信は受信トレイ、送信は送信済みと、ユーザーフォルダにだけ移せる)</summary>
    Private Shared Function CanMoveTo(m As MailItem, folder As FolderItem) As Boolean
        If m.IsReserved Then Return False          ' 送信予約は移動できない(取り消すだけ)
        If m.Folder = folder.Id Then Return False
        Select Case folder.Kind
            Case FolderKind.User
                Return True
            Case FolderKind.Inbox
                Return Not m.IsSent
            Case FolderKind.Sent
                Return m.IsSent
            Case Else
                Return False
        End Select
    End Function

    ''' <summary>items をすべて folder へドラッグ&ドロップできるか(ごみ箱は、すべてがごみ箱の外にあるとき)</summary>
    Private Shared Function CanDropOn(items As List(Of MailItem), folder As FolderItem) As Boolean
        If items.Count = 0 Then Return False
        If items.Any(Function(m) m.IsReserved) Then Return False
        If folder.Kind = FolderKind.Trash Then Return items.All(Function(m) m.Folder <> FolderStore.TrashId)
        Return items.All(Function(m) CanMoveTo(m, folder))
    End Function

    ''' <summary>選択中のすべてのメッセージを移動できるフォルダ</summary>
    Private Function MoveTargets(items As List(Of MailItem)) As List(Of FolderItem)
        Dim res As New List(Of FolderItem)
        If items.Count = 0 Then Return res
        For Each f In New FolderItem() {_fInbox, _fSent}
            Dim target = f
            If items.All(Function(m) CanMoveTo(m, target)) Then res.Add(f)
        Next
        ' 受信と送信が混ざっているときは、それぞれの標準フォルダ(受信トレイ/送信済み)へ戻す項目を出す
        If items.Any(Function(m) m.IsSent) AndAlso items.Any(Function(m) Not m.IsSent) AndAlso items.All(Function(m) m.Folder.Length > 0) Then
            res.Add(_fDefault)
        End If
        For Each f In _folders
            Dim target = f
            If items.All(Function(m) CanMoveTo(m, target)) Then res.Add(f)
        Next
        Return res
    End Function

    ''' <summary>複数のメッセージをまとめて folderId のフォルダへ移動する(空のIDなら、それぞれの標準フォルダへ)</summary>
    Private Sub MoveMessages(items As List(Of MailItem), folderId As String)
        Dim moving = items.Where(Function(m) m.Folder <> folderId).ToList()
        If moving.Count = 0 Then Return
        For Each m In moving
            m.Folder = folderId
        Next
        SaveMailFiles(moving)
        FillList()

        Dim dest As String
        If folderId.Length > 0 Then
            Dim fo = _folders.FirstOrDefault(Function(f) f.Id = folderId)
            dest = If(fo IsNot Nothing, fo.Name, folderId)
        ElseIf moving.All(Function(m) m.IsSent) Then
            dest = _fSent.Name
        ElseIf moving.All(Function(m) Not m.IsSent) Then
            dest = _fInbox.Name
        Else
            dest = _fInbox.Name & " / " & _fSent.Name
        End If
        Log($"{moving.Count} 件のメッセージを「{dest}」へ移動しました。")
    End Sub

    ''' <summary>移動先のメニューを作る(ツールバーの「移動」と、右クリックメニューで共用)</summary>
    Private Sub BuildMoveMenu(items As ToolStripItemCollection, ms As List(Of MailItem))
        items.Clear()
        For Each f In MoveTargets(ms)
            Dim mi As New ToolStripMenuItem(f.Name)
            mi.Tag = f
            AddHandler mi.Click, AddressOf OnMoveClick
            items.Add(mi)
        Next
        If items.Count = 0 Then items.Add(New ToolStripMenuItem("(移動先のフォルダがありません)") With {.Enabled = False})
    End Sub

    Private Sub OnMoveMenuOpening(sender As Object, e As EventArgs)
        BuildMoveMenu(btnMove.DropDownItems, SelectedMessages())
    End Sub

    Private Sub OnMoveClick(sender As Object, e As EventArgs)
        Dim f = DirectCast(DirectCast(sender, ToolStripMenuItem).Tag, FolderItem)
        Dim ms = SelectedMessages()
        If ms.Count > 0 Then MoveMessages(ms, f.Id)
    End Sub

    ''' <summary>メッセージの右クリックメニュー</summary>
    Private Sub OnMessageContextOpening(sender As Object, e As CancelEventArgs)
        Dim ms = SelectedMessages()
        If ms.Count = 0 Then
            e.Cancel = True
            Return
        End If
        Dim isOne = (ms.Count = 1)
        ctxMessages.Items.Clear()
        Dim miReply As New ToolStripMenuItem("返信") With {.Enabled = isOne AndAlso Not ms(0).IsSent}
        AddHandler miReply.Click, AddressOf OnReply
        Dim miVerify As New ToolStripMenuItem("署名を検証") With {.Enabled = isOne AndAlso Not ms(0).IsSent}
        AddHandler miVerify.Click, AddressOf OnVerify
        ' 差出人を連絡先に追加(受信メッセージを1通だけ選んでいて、差出人がまだ登録されていないときに使える)
        Dim senderKnown = isOne AndAlso Not ms(0).IsSent AndAlso _contacts.Any(Function(x) x.Id = ms(0).PeerId)
        Dim miAddSender As New ToolStripMenuItem(If(senderKnown, "差出人は連絡先に登録済み", "差出人を連絡先に追加...")) With {
            .Enabled = isOne AndAlso Not ms(0).IsSent AndAlso Not senderKnown}
        AddHandler miAddSender.Click, AddressOf OnAddSenderToContacts
        Dim miMove As New ToolStripMenuItem("移動")
        BuildMoveMenu(miMove.DropDownItems, ms)
        Dim allTrash = ms.All(Function(m) m.Folder = FolderStore.TrashId)
        Dim miDelete As New ToolStripMenuItem(If(ms.All(Function(m) m.IsReserved), "予約を取り消す...", If(allTrash, "完全に削除...", "削除")))
        AddHandler miDelete.Click, AddressOf OnDeleteMessage
        ctxMessages.Items.AddRange(New ToolStripItem() {miReply, miVerify, miAddSender, New ToolStripSeparator(), miMove, miDelete})
    End Sub

    ' ---- ドラッグ&ドロップ: メッセージ(複数選択中ならまとめて)をフォルダ一覧へドラッグして移動 ----

    Private Sub OnMessageDrag(sender As Object, e As ItemDragEventArgs)
        Dim it = TryCast(e.Item, ListViewItem)
        If it Is Nothing Then Return
        ' ドラッグした項目が選択中なら、選択中のすべてをまとめて動かす
        _dragging = If(it.Selected, SelectedMessages(), New List(Of MailItem) From {DirectCast(it.Tag, MailItem)})
        Try
            lvMessages.DoDragDrop("P2PMessenger.MailItem", DragDropEffects.Move)
        Finally
            _dragging = Nothing
        End Try
    End Sub

    Private Function FolderAt(screenX As Integer, screenY As Integer) As FolderItem
        Dim p = lstFolders.PointToClient(New Point(screenX, screenY))
        Dim idx = lstFolders.IndexFromPoint(p)
        If idx < 0 Then Return Nothing
        Return TryCast(lstFolders.Items(idx), FolderItem)
    End Function

    Private Sub OnFolderDragOver(sender As Object, e As DragEventArgs)
        e.Effect = DragDropEffects.None
        Dim f = FolderAt(e.X, e.Y)
        If _dragging IsNot Nothing AndAlso f IsNot Nothing AndAlso CanDropOn(_dragging, f) Then e.Effect = DragDropEffects.Move
    End Sub

    Private Sub OnFolderDragDrop(sender As Object, e As DragEventArgs)
        Dim ms = _dragging
        Dim f = FolderAt(e.X, e.Y)
        If ms Is Nothing OrElse f Is Nothing OrElse Not CanDropOn(ms, f) Then Return
        ' ドラッグ操作が終わってから一覧を更新する(ごみ箱へのドロップは「削除」と同じ)
        If f.Kind = FolderKind.Trash Then
            BeginInvoke(Sub() MoveToTrash(ms))
        Else
            BeginInvoke(Sub() MoveMessages(ms, f.Id))
        End If
    End Sub

    ' ====================== フォルダの作成・変更・削除 ======================

    Private Shared Function CleanFolderName(s As String) As String
        Return New String(s.Where(Function(c) Not Char.IsControl(c)).ToArray()).Trim()
    End Function

    ''' <summary>フォルダ名として使えるか。問題があればその理由、なければ Nothing</summary>
    Private Function ValidateFolderName(name As String, target As FolderItem) As String
        If name.Length = 0 Then Return "フォルダ名を入力してください。"
        If name.Length > 40 Then Return "フォルダ名は40文字以内にしてください。"
        Dim reserved = New String() {_fInbox.Name, _fSent.Name, _fOutbox.Name, _fTrash.Name, _fLog.Name}
        If reserved.Contains(name) OrElse _folders.Any(Function(f) f IsNot target AndAlso String.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) Then
            Return "同じ名前のフォルダがあります。"
        End If
        Return Nothing
    End Function

    Private Sub BuildFolderMenu(items As ToolStripItemCollection)
        items.Clear()
        Dim isUser = (_current IsNot Nothing AndAlso _current.Kind = FolderKind.User)
        Dim miNew As New ToolStripMenuItem("新しいフォルダ...")
        AddHandler miNew.Click, AddressOf OnFolderNew
        Dim miRename As New ToolStripMenuItem("名前を変更...") With {.Enabled = isUser}
        AddHandler miRename.Click, AddressOf OnFolderRename
        Dim miDelete As New ToolStripMenuItem("フォルダを削除...") With {.Enabled = isUser}
        AddHandler miDelete.Click, AddressOf OnFolderDelete
        Dim miEmpty As New ToolStripMenuItem("ごみ箱を空にする...")
        AddHandler miEmpty.Click, AddressOf OnEmptyTrash
        items.Add(miNew)
        items.Add(miRename)
        items.Add(miDelete)
        items.Add(New ToolStripSeparator())
        items.Add(miEmpty)
    End Sub

    Private Sub OnFolderMenuOpening(sender As Object, e As EventArgs)
        BuildFolderMenu(btnFolder.DropDownItems)
    End Sub

    Private Sub OnFolderContextOpening(sender As Object, e As CancelEventArgs)
        BuildFolderMenu(ctxFolders.Items)
    End Sub

    ''' <summary>右クリックしたフォルダを選択状態にしてからメニューを開く</summary>
    Private Sub OnFolderMouseDown(sender As Object, e As MouseEventArgs)
        If e.Button <> MouseButtons.Right Then Return
        Dim idx = lstFolders.IndexFromPoint(e.Location)
        If idx >= 0 Then lstFolders.SelectedIndex = idx
    End Sub

    Private Sub OnFolderNew(sender As Object, e As EventArgs)
        Dim name = PromptForm.Ask(Me, "新しいフォルダ", "フォルダ名を入力してください", "")
        If name Is Nothing Then Return
        name = CleanFolderName(name)
        Dim err = ValidateFolderName(name, Nothing)
        If err IsNot Nothing Then
            MessageBox.Show(Me, err, Text)
            Return
        End If
        Dim f As New FolderItem() With {.Kind = FolderKind.User, .Id = FolderStore.NewId(), .Name = name}
        _folders.Add(f)
        SaveFolders()
        RebuildFolderList(f)
        Log($"フォルダ「{name}」を作成しました。")
    End Sub

    Private Sub OnFolderRename(sender As Object, e As EventArgs)
        If _current Is Nothing OrElse _current.Kind <> FolderKind.User Then Return
        Dim name = PromptForm.Ask(Me, "フォルダ名の変更", "新しいフォルダ名を入力してください", _current.Name)
        If name Is Nothing Then Return
        name = CleanFolderName(name)
        Dim err = ValidateFolderName(name, _current)
        If err IsNot Nothing Then
            MessageBox.Show(Me, err, Text)
            Return
        End If
        Dim f = _current
        Log($"フォルダ「{f.Name}」の名前を「{name}」に変更しました。")
        f.Name = name
        SaveFolders()
        RebuildFolderList(f)
    End Sub

    ''' <summary>フォルダの削除: 中のメッセージは消さず、受信は受信トレイ・送信は送信済みへ戻す</summary>
    Private Sub OnFolderDelete(sender As Object, e As EventArgs)
        If _current Is Nothing OrElse _current.Kind <> FolderKind.User Then Return
        Dim f = _current
        Dim inside = _inbox.Concat(_sent).Where(Function(m) m.Folder = f.Id).ToList()
        Dim msg = $"フォルダ「{f.Name}」を削除します。" & vbCrLf & vbCrLf &
                  If(inside.Count > 0,
                     $"中の {inside.Count} 件のメッセージは削除されず、受信したものは「{_fInbox.Name}」へ、送信したものは「{_fSent.Name}」へ戻ります。",
                     "このフォルダにメッセージはありません。") &
                  If(_rules.Any(Function(r) r.TargetFolderId = f.Id),
                     vbCrLf & vbCrLf & $"このフォルダを移動先にしている振り分けルール {_rules.Where(Function(r) r.TargetFolderId = f.Id).Count()} 件は、無効になります。", "") &
                  vbCrLf & vbCrLf & "よろしいですか?"
        If MessageBox.Show(Me, msg, "フォルダの削除", MessageBoxButtons.YesNo, MessageBoxIcon.Question) <> DialogResult.Yes Then Return

        For Each m In inside
            m.Folder = ""
        Next
        If inside.Any(Function(m) Not m.IsSent) Then SaveMailFile(False)
        If inside.Any(Function(m) m.IsSent) Then SaveMailFile(True)
        _folders.Remove(f)
        SaveFolders()
        Dim affected = _rules.Where(Function(r) r.TargetFolderId = f.Id).ToList()
        For Each r In affected
            r.Enabled = False
            r.TargetFolderId = ""
        Next
        If affected.Count > 0 Then SaveRules()
        RebuildFolderList(_fInbox)
        Log($"フォルダ「{f.Name}」を削除しました。")
    End Sub

    ' ====================== 送信予約 ======================

    ''' <summary>ComposeForm の「予約として保存」: 今は送らず、相手が問い合わせてきたときに渡すために保管する</summary>
    Private Sub ReserveFromCompose(c As Contact, msg As String)
        Messenger.CheckTextSize(msg)
        Dim item = _reservations.Add(c.Id, msg, _peers.MaxReserved)
        If _current IsNot Nothing AndAlso _current.Kind = FolderKind.Outbox Then lvMessages.Items.Insert(0, MakeRow(item))
        UpdateStatus()
        Log($"[予約 → {c.Name}] 予約しました。相手が問い合わせてきたときに送信します。" &
            If(chkListen.Checked, "", "(この端末が受信待機中でないと、相手は問い合わせできません)"))
    End Sub

    ''' <summary>予約メッセージを相手に渡せたとき(別のスレッドから呼ばれる): 送信済みに記録する</summary>
    Private Sub OnReservedDelivered(item As MailItem)
        If InvokeRequired Then
            Try
                BeginInvoke(Sub() OnReservedDelivered(item))
            Catch ex As InvalidOperationException
            End Try
            Return
        End If
        Dim sentItem As New MailItem() With {.Time = DateTime.Now, .PeerId = item.PeerId, .Text = item.Text, .IsSent = True}
        _sent.Add(sentItem)
        SaveItem(_sentPath, sentItem)
        If _current IsNot Nothing AndAlso (_current.Kind = FolderKind.Outbox OrElse _current.Kind = FolderKind.Sent) Then FillList()
        UpdateStatus()
        Log($"予約メッセージを {WhoLabel(item.PeerId, "")} へ送信しました(相手の問い合わせに応答)。")
    End Sub

    ''' <summary>予約の取り消し(確認のうえ、予約から削除する。相手には送られない)</summary>
    Private Sub CancelReservations(items As List(Of MailItem))
        Dim what = If(items.Count = 1, "この予約", $"選択した {items.Count} 件の予約")
        Dim msg = $"{what}を取り消します。" & vbCrLf & "メッセージは相手に送られず、削除されます。元に戻せません。" & vbCrLf & vbCrLf & "取り消しますか?"
        If MessageBox.Show(Me, msg, "予約の取り消し", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then Return
        Try
            For Each m In items
                _reservations.Remove(m)
            Next
        Catch ex As Exception
            Log("予約を更新できませんでした: " & ex.Message)
            MessageBox.Show(Me, "予約を更新できませんでした: " & ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
        FillList()
        UpdateStatus()
        Log($"{items.Count} 件の予約を取り消しました。")
    End Sub

    ' ====================== 予約メッセージの問い合わせ(受け取る側) ======================
    ' 自分宛の予約を持っている相手(連絡先)へこちらから接続して受け取る。外向きの接続だけなので、自分のポート開放は不要。
    ' 渡す側は受信待機中(ポート開放済み)で、ホストまたは連絡先のアドレスで見つけられる必要がある。

    ''' <summary>設定パネルの説明文。問い合わせの対象にしている連絡先の人数を出す</summary>
    Private Sub UpdatePollInfo()
        Dim n = _contacts.Where(Function(c) c.Poll).Count()
        lblPollInfo.Text = $"予約を問い合わせる設定の連絡先 {n} 人に、{_peers.PollIntervalMinutes} 分ごとに問い合わせます(自分のポート開放は不要)"
    End Sub

    Private Sub OnPollToggled(sender As Object, e As EventArgs)
        If chkPoll.Checked Then
            _peers.PollEnabled = True
            _peers.Save()
            tmrPoll.Interval = 3000          ' 最初は、すぐ(3秒後)に1回問い合わせる
            tmrPoll.Start()
            Log($"予約メッセージの問い合わせを開始しました(連絡先に {_peers.PollIntervalMinutes} 分ごと)。")
        Else
            tmrPoll.Stop()
            _peers.PollEnabled = False
            _peers.Save()
            Log("予約メッセージの問い合わせを停止しました。")
        End If
    End Sub

    Private Sub OnPollTick(sender As Object, e As EventArgs)
        tmrPoll.Interval = Math.Max(1, _peers.PollIntervalMinutes) * 60 * 1000
        StartPoll(False)
    End Sub

    ''' <summary>連絡先の全員に問い合わせる。manual=True(ボタン)のときは、結果をすべてログに出す</summary>
    Private Sub StartPoll(manual As Boolean)
        If _polling Then
            If manual Then Log("予約メッセージの問い合わせは、すでに実行中です。")
            Return
        End If
        Dim selfId = _msgr.Self.Id
        ' 連絡先ごとの設定で「予約を問い合わせる」にした相手だけに問い合わせる
        Dim targets = _contacts.Where(Function(c) c.Id <> selfId AndAlso c.Poll).ToList()
        If targets.Count = 0 Then
            If manual Then Log(If(_contacts.Count = 0, "問い合わせ先の連絡先がありません。", "予約を問い合わせる設定の連絡先がありません。連絡先の画面で、相手ごとに設定してください。"))
            Return
        End If
        _polling = True
        If manual Then Log($"連絡先 {targets.Count} 人に、自分宛の予約メッセージを問い合わせます…")
        Dim t = Task.Run(Sub() RunPoll(targets, manual))
    End Sub

    Private Sub RunPoll(targets As List(Of Contact), manual As Boolean)
        Dim received = 0
        Dim reachable = 0
        Dim tally As New Object()
        Try
            _hostClient.PollContacts(_msgr, targets,
                Sub(c, n)
                    SyncLock tally
                        If n >= 0 Then reachable += 1
                        If n > 0 Then received += n
                    End SyncLock
                    OnPolled(c, n, manual)
                End Sub)
            If manual Then Log($"問い合わせが終わりました(受け取り {received} 件 / つながった相手 {reachable} 人 / {targets.Count} 人)。")
        Catch ex As Exception
            Log("予約メッセージの問い合わせでエラー: " & ex.Message)
        Finally
            Try
                BeginInvoke(Sub() _polling = False)
            Catch ex As InvalidOperationException
            End Try
        End Try
    End Sub

    ''' <summary>1人分の結果。つながらない状態が続くときに、毎回ログを出さないよう、状態が変わったときだけ出す</summary>
    Private Sub OnPolled(c As Contact, n As Integer, manual As Boolean)
        Dim previous As Integer = 0
        Dim known As Boolean
        SyncLock _pollLock
            known = _pollStatus.TryGetValue(c.Id, previous)
            _pollStatus(c.Id) = If(n < 0, -1, 0)
        End SyncLock
        If n > 0 Then
            Log($"{c.Name} から予約メッセージを {n} 件受け取りました。")
        ElseIf n < 0 Then
            If manual OrElse Not known OrElse previous >= 0 Then
                Log($"{c.Name} には今つながりません(相手が受信待機中でないか、見つかりません)。次回も自動で確認します。")
            End If
        ElseIf manual Then
            Log($"{c.Name}: 自分宛の予約メッセージはありません。")
        End If
    End Sub

    ' ====================== 振り分けルール ======================

    ''' <summary>ルールの移動先として、そのフォルダが今も存在するか(ユーザーフォルダ、または ごみ箱)</summary>
    Private Function IsValidTarget(folderId As String) As Boolean
        Return folderId = FolderStore.TrashId OrElse _folders.Any(Function(f) f.Id = folderId)
    End Function

    Private Function FolderNameOf(folderId As String) As String
        If folderId = FolderStore.TrashId Then Return _fTrash.Name
        Dim f = _folders.FirstOrDefault(Function(x) x.Id = folderId)
        Return If(f IsNot Nothing, f.Name, "(不明なフォルダ)")
    End Function

    ''' <summary>受信メッセージに当てはまる振り分けルール(上から順に調べて、最初に条件を満たしたもの)。なければ Nothing</summary>
    Private Function FindRuleFor(item As MailItem) As MailRule
        Dim c = _contacts.FirstOrDefault(Function(x) x.Id = item.PeerId)
        Dim registered = (c IsNot Nothing)
        ' 登録済みの人は連絡先の表示名、未登録の人は相手が名乗っている表示名と比べる
        Dim fromName = If(registered, c.Name, item.ClaimedName)
        Return RuleEngine.FindRule(_rules, item.PeerId, fromName, registered, item.Text, AddressOf IsValidTarget)
    End Function

    Private Sub SaveRules()
        Try
            RuleStore.Save(_rulesPath, _rules)
        Catch ex As Exception
            Log("振り分けルールを保存できませんでした: " & ex.Message)
            MessageBox.Show(Me, "振り分けルールを保存できませんでした: " & ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
    End Sub

    Private Sub OpenRules()
        ' 移動先に選べるのは、ユーザーフォルダと ごみ箱
        Dim targets = _folders.ToList()
        targets.Add(_fTrash)
        Using f As New RulesForm(_rules, targets, _contacts, AddressOf SaveRules, AddressOf ApplyRulesToInbox)
            f.ShowDialog(Me)
        End Using
    End Sub

    ''' <summary>
    ''' 「受信トレイに今すぐ適用」: 受信トレイに残っているメッセージのうち、ルールに当てはまるものを、まとめて移動する。
    ''' (新しく受信するメッセージには、受信のたびに自動で適用される)
    ''' </summary>
    Private Sub ApplyRulesToInbox()
        Dim moves As New List(Of KeyValuePair(Of MailItem, MailRule))
        For Each item In _inbox.Where(Function(m) m.Folder.Length = 0).ToList()
            Dim rule = FindRuleFor(item)
            If rule IsNot Nothing Then moves.Add(New KeyValuePair(Of MailItem, MailRule)(item, rule))
        Next
        If moves.Count = 0 Then
            MessageBox.Show(Me, "ルールに当てはまるメッセージは、受信トレイにありません。", "振り分けの適用", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        If MessageBox.Show(Me, $"受信トレイの {moves.Count} 件のメッセージを、ルールに従って移動します。よろしいですか?", "振り分けの適用",
                           MessageBoxButtons.YesNo, MessageBoxIcon.Question) <> DialogResult.Yes Then Return
        For Each mv In moves
            mv.Key.Folder = mv.Value.TargetFolderId
            If mv.Value.TargetFolderId = FolderStore.TrashId Then _unread.Remove(mv.Key)
        Next
        SaveMailFile(False)
        FillList()
        UpdateStatus()
        Log($"振り分けルールを受信トレイに適用し、{moves.Count} 件のメッセージを移動しました。")
    End Sub

    ' ====================== 作成・返信・連絡先 ======================

    Private Sub OpenCompose(selected As Contact)
        If _contacts.Count = 0 Then
            MessageBox.Show(Me, "宛先がありません。先に連絡先を登録してください。", Text)
            OpenContacts("")
            If _contacts.Count = 0 Then Return
        End If
        Using f As New ComposeForm(_contacts, selected, AddressOf SendFromCompose, AddressOf ReserveFromCompose)
            f.ShowDialog(Me)
        End Using
    End Sub

    Private Sub OnReply(sender As Object, e As EventArgs)
        Dim m = SelectedMessage()
        If m Is Nothing OrElse m.IsSent Then Return
        Dim c = _contacts.FirstOrDefault(Function(x) x.Id = m.PeerId)
        If c Is Nothing Then
            Dim hint = If(m.ClaimedName.Length > 0, "相手が名乗っている表示名を入力済みです。必要なら修正して登録してください。", "表示名を付けて登録すると返信できます。")
            MessageBox.Show(Me, "この差出人は連絡先に未登録です。" & hint, Text)
            OpenContacts(m.ClaimedName & "@" & m.PeerId)
            c = _contacts.FirstOrDefault(Function(x) x.Id = m.PeerId)
            If c Is Nothing Then Return
        End If
        OpenCompose(c)
    End Sub

    ''' <summary>右クリックの「差出人を連絡先に追加」: 相手が名乗っている表示名と実効IDを入れた状態で、連絡先の登録画面を開く</summary>
    Private Sub OnAddSenderToContacts(sender As Object, e As EventArgs)
        Dim m = SelectedMessage()
        If m Is Nothing OrElse m.IsSent Then Return
        If _contacts.Any(Function(x) x.Id = m.PeerId) Then Return
        OpenContacts(m.ClaimedName & "@" & m.PeerId)
    End Sub

    Private Sub OpenContacts(prefillId As String)
        Using f As New ContactsForm(_contacts, _msgr.Self.Id, CInt(nudPort.Value), AddressOf SaveContacts, prefillId)
            f.ShowDialog(Me)
        End Using
        FillList()      ' 表示名が変わった可能性があるので一覧を作り直す
        UpdatePollInfo()
    End Sub

    ''' <summary>ComposeForm から呼ばれる送信処理。失敗時は例外を投げて画面に表示させる</summary>
    Private Async Function SendFromCompose(c As Contact, msg As String) As Task
        Dim cid = c.Id
        Dim endpoint = c.Endpoint
        Dim route = ""
        Try
            ' アドレス登録があればそこへ、無い/届かない場合はホストで検索して送る
            route = Await Task.Run(Function() _hostClient.SendMessage(_msgr, cid, endpoint, msg))
        Catch ex As Exception
            Log($"送信できませんでした ({c.Name}): {ex.Message}")
            Throw
        End Try
        Dim item As New MailItem() With {.Time = DateTime.Now, .PeerId = cid, .Text = msg, .IsSent = True}
        _sent.Add(item)
        SaveItem(_sentPath, item)
        If _current IsNot Nothing AndAlso _current.Kind = FolderKind.Sent Then lvMessages.Items.Insert(0, MakeRow(item))
        Log($"[送信 → {c.Name}] 送信しました({route})")
    End Function

    ' ====================== 受信 ======================

    Private Sub OnMessageReceived(fromId As String, text As String, ev As Evidence)
        If InvokeRequired Then
            Try
                BeginInvoke(Sub() OnMessageReceived(fromId, text, ev))
            Catch ex As InvalidOperationException
            End Try
            Return
        End If
        Dim item As New MailItem() With {.Time = DateTime.Now, .PeerId = fromId, .Text = text, .Evidence = ev}
        ' 振り分けルール: 条件を満たしたら、指定されたフォルダへ移動してから保存する
        Dim rule = FindRuleFor(item)
        If rule IsNot Nothing Then item.Folder = rule.TargetFolderId
        _inbox.Add(item)
        SaveItem(_inboxPath, item)
        If item.Folder <> FolderStore.TrashId Then _unread.Add(item)      ' ごみ箱へ振り分けたものは、未読にしない
        ' いま開いているフォルダが、メッセージの行き先なら、一覧に追加する
        If _current IsNot Nothing Then
            Dim shown = (_current.Kind = FolderKind.Inbox AndAlso item.Folder.Length = 0) OrElse
                        ((_current.Kind = FolderKind.User OrElse _current.Kind = FolderKind.Trash) AndAlso _current.Id = item.Folder)
            If shown Then lvMessages.Items.Insert(0, MakeRow(item))
        End If
        UpdateStatus()
        Dim who = WhoLabel(fromId, If(ev IsNot Nothing, ev.ClaimedName, ""))
        If rule IsNot Nothing Then
            Log($"メッセージを受信しました: {who}  → 振り分けルール「{rule.Name}」で「{FolderNameOf(rule.TargetFolderId)}」へ移動しました。")
        Else
            Log($"メッセージを受信しました: {who}")
        End If
    End Sub

    Private Sub SaveItem(path As String, item As MailItem)
        Try
            MailStore.Append(path, item)
        Catch ex As Exception
            Log("メッセージを保存できませんでした: " & ex.Message)
        End Try
    End Sub

    ' ====================== 接続設定 ======================

    ''' <summary>「メッセージを受信する」チェック: 状態を Peers.ini に記録し、次回起動時にも復元する</summary>
    Private Sub OnListenToggled(sender As Object, e As EventArgs)
        If chkListen.Checked Then
            Try
                _msgr.StartListening(CInt(nudPort.Value))
                nudPort.Enabled = False
                _peers.ListenEnabled = True
                _peers.ListenPort = CInt(nudPort.Value)
                _peers.Save()
                Log($"ポート {nudPort.Value} で受信待機中です。(ルーターのポート開放とファイアウォールの許可が必要です)")
            Catch ex As Exception
                Log("受信を開始できませんでした: " & ex.Message)
                RemoveHandler chkListen.CheckedChanged, AddressOf OnListenToggled
                chkListen.Checked = False
                AddHandler chkListen.CheckedChanged, AddressOf OnListenToggled
            End Try
        Else
            _msgr.StopListening()
            nudPort.Enabled = True
            _peers.ListenEnabled = False
            _peers.Save()
            Log("受信を停止しました。")
        End If
        If Not _restoring Then ApplyState()
    End Sub

    Private Sub OnHostToggled(sender As Object, e As EventArgs)
        If chkHost.Checked Then
            Try
                _hostService.StartService(CInt(nudHostPort.Value))
                nudHostPort.Enabled = False
                _peers.HostEnabled = True
                _peers.HostPort = CInt(nudHostPort.Value)
                _peers.Save()
                Log($"ホスト機能を開始しました(ポート {nudHostPort.Value})。ルーターのポート開放とファイアウォールの許可が必要です。")
            Catch ex As Exception
                Log("ホスト機能を開始できませんでした: " & ex.Message)
                RemoveHandler chkHost.CheckedChanged, AddressOf OnHostToggled
                chkHost.Checked = False
                AddHandler chkHost.CheckedChanged, AddressOf OnHostToggled
            End Try
        Else
            _hostService.StopService()
            nudHostPort.Enabled = True
            _peers.HostEnabled = False
            _peers.Save()
            Log("ホスト機能を停止しました。")
        End If
        If Not _restoring Then ApplyState()
    End Sub

    ''' <summary>ポート番号が変更されたら Peers.ini に記録する(次回起動時に同じ番号を使う)</summary>
    Private Sub OnPortChanged(sender As Object, e As EventArgs)
        _peers.ListenPort = CInt(nudPort.Value)
        _peers.HostPort = CInt(nudHostPort.Value)
        _peers.Save()
    End Sub

    Private Sub ApplyState()
        UpdateAnnounce()
        UpdateStatus()
    End Sub

    ''' <summary>受信中・ホスト提供中なら、一定間隔(NotifyIntervalMinutes)で自分の情報をホストへ通知する</summary>
    Private Sub UpdateAnnounce()
        _msgPortAnnounce = If(chkListen.Checked, CInt(nudPort.Value), 0)
        _hostPortAnnounce = If(chkHost.Checked, CInt(nudHostPort.Value), 0)
        If _msgPortAnnounce = 0 AndAlso _hostPortAnnounce = 0 Then
            _announceTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite)
        Else
            _announceTimer.Change(0, _peers.NotifyIntervalMinutes * 60 * 1000)     ' 通知間隔は Peers.ini の NotifyIntervalMinutes
        End If
    End Sub

    Private Sub OnAnnounceTick(state As Object)
        Dim m = _msgPortAnnounce
        Dim h = _hostPortAnnounce
        If m = 0 AndAlso h = 0 Then Return
        Try
            _hostClient.RegisterAll(m, h)
        Catch ex As Exception
            Log("ホストへの通知でエラー: " & ex.Message)
        End Try
    End Sub

    Private Sub UpdateStatus()
        lblListen.Text = If(chkListen.Checked, $"受信待機中 (ポート {nudPort.Value})", "受信: 停止中")
        lblHostState.Text = If(chkHost.Checked, $"ホスト提供中 (ポート {nudHostPort.Value})", "ホスト: 停止中")
        Dim notes As New List(Of String)
        If _unread.Count > 0 Then notes.Add($"未読 {_unread.Count} 件")
        If _reservations IsNot Nothing AndAlso _reservations.Count > 0 Then notes.Add($"予約中 {_reservations.Count} 件")
        lblUnread.Text = String.Join(" / ", notes)
    End Sub

    Private Sub OnNameChanged(sender As Object, e As EventArgs)
        _msgr.Self.DisplayName = CleanDisplayName(txtName.Text)
        RefreshMyId()
    End Sub

    Private Sub RefreshMyId()
        txtMyId.Text = _msgr.Self.PublicId
    End Sub

    ' ====================== ログ・連絡先の保存 ======================

    Private Sub Log(s As String)
        If InvokeRequired Then
            Try
                BeginInvoke(Sub() Log(s))
            Catch ex As InvalidOperationException
                ' 終了処理中などで画面が使えない場合は無視する
            End Try
            Return
        End If
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}{vbCrLf}")
        _logLines += 1 + s.Count(Function(c) c = ControlChars.Lf)
        TrimLog()
    End Sub

    ''' <summary>ログが最大行数(Peers.ini の [Log] MaxLines)を超えたら、古い行から消す</summary>
    Private Sub TrimLog()
        Dim max = If(_peers IsNot Nothing, _peers.LogMaxLines, 1000)
        ' 件数が多い設定のときは、少し超えるまで待ってまとめて消す(毎回の書き換えを避ける)
        Dim tolerance = If(max <= 5000, 0, max \ 20)
        If _logLines <= max + tolerance Then Return
        txtLog.Text = DropFirstLines(txtLog.Text, _logLines - max)
        _logLines = max
        txtLog.SelectionStart = txtLog.TextLength
        txtLog.ScrollToCaret()
    End Sub

    Private Sub LoadContacts()
        _contacts.AddRange(ContactStore.Load(_contactsPath))
    End Sub

    Private Sub SaveContacts()
        Try
            ContactStore.Save(_contactsPath, _contacts)
        Catch ex As Exception
            Log("連絡先を保存できませんでした: " & ex.Message)
            MessageBox.Show(Me, "連絡先を保存できませんでした: " & ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        _msgr.Self.Save(_userPath)
        _msgr.Dispose()
        _announceTimer.Dispose()
        tmrPoll.Stop()
        _hostService.Dispose()
        _boldFont.Dispose()
        MyBase.OnFormClosing(e)
    End Sub

End Class
