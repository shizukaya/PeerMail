Option Strict On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Net
Imports System.Windows.Forms

''' <summary>連絡先の追加・更新・削除。IP:ポートは省略可能(省略するとホストで検索する)</summary>
Public Class ContactsForm
    Inherits Form

    Private ReadOnly _contacts As List(Of Contact)
    Private ReadOnly _selfId As String
    Private ReadOnly _listenPort As Integer
    Private ReadOnly _save As Action
    Private ReadOnly _prefill As String

    Private ReadOnly lst As New ListBox() With {.Dock = DockStyle.Fill, .IntegralHeight = False, .HorizontalScrollbar = True}
    Private ReadOnly txtNewId As New TextBox() With {.Width = 740, .PlaceholderText = "公開ID  例: alice@4RkT6G7u9Lm..."}
    Private ReadOnly txtNewAddr As New TextBox() With {.Width = 740, .PlaceholderText = "相手のIP:ポート (空欄ならホストで検索)"}
    Private ReadOnly chkPoll As New CheckBox() With {.Text = "この相手に、自分宛の予約メッセージを問い合わせる", .AutoSize = True, .Checked = True}
    Private ReadOnly btnAdd As New Button() With {.Text = "追加 / 更新", .AutoSize = True}
    Private ReadOnly btnDel As New Button() With {.Text = "選択を削除", .AutoSize = True}
    Private ReadOnly btnClose As New Button() With {.Text = "閉じる", .AutoSize = True, .DialogResult = DialogResult.OK}

    Public Sub New(contacts As List(Of Contact), selfId As String, listenPort As Integer, save As Action, prefillId As String)
        Text = "連絡先"
        Font = New Font("Yu Gothic UI", 10.0F)
        ClientSize = New Size(780, 460)
        MinimumSize = New Size(780, 460)      ' 入力欄(幅740)が収まる大きさより、小さくはできない
        StartPosition = FormStartPosition.CenterParent
        Padding = New Padding(8)
        _contacts = contacts
        _selfId = selfId
        _listenPort = listenPort
        _save = save
        _prefill = prefillId

        Dim row As New FlowLayoutPanel() With {.AutoSize = True, .WrapContents = False}
        row.Controls.AddRange(New Control() {btnAdd, btnDel, btnClose})
        Dim pnlAdd As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .AutoSize = True,
            .FlowDirection = FlowDirection.TopDown, .WrapContents = False}
        pnlAdd.Controls.AddRange(New Control() {
            New Label() With {.Text = "連絡先を追加 / 更新", .AutoSize = True}, txtNewId, txtNewAddr, chkPoll, row})

        Controls.Add(lst)         ' Fill は最初に追加する
        Controls.Add(pnlAdd)

        CancelButton = btnClose
        AddHandler btnAdd.Click, AddressOf OnAdd
        AddHandler btnDel.Click, AddressOf OnDelete
        AddHandler lst.SelectedIndexChanged, AddressOf OnSelect
        RefreshList()
    End Sub

    Protected Overrides Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)
        If _prefill.Length > 0 Then
            ' 返信で未登録の差出人を登録する場合: 「@実効ID」を入れてあるので、先頭に表示名を付けてもらう
            txtNewId.Text = _prefill
            txtNewId.Focus()
            txtNewId.SelectionStart = 0
            txtNewId.SelectionLength = 0
        End If
    End Sub

    Private Sub RefreshList()
        lst.Items.Clear()
        lst.Items.AddRange(_contacts.Cast(Of Object)().ToArray())
    End Sub

    ' 選んだ連絡先の内容を入力欄に出して、そのまま編集できるようにする
    Private Sub OnSelect(sender As Object, e As EventArgs)
        Dim c = TryCast(lst.SelectedItem, Contact)
        If c Is Nothing Then Return
        txtNewId.Text = c.Name & "@" & c.Id
        txtNewAddr.Text = c.Endpoint
        chkPoll.Checked = c.Poll
    End Sub

    ' 入力欄を空に戻す(予約の問い合わせは、新しい連絡先の既定どおり「問い合わせる」)
    Private Sub ClearInputs()
        txtNewId.Clear()
        txtNewAddr.Clear()
        chkPoll.Checked = True
    End Sub

    Private Sub OnAdd(sender As Object, e As EventArgs)
        Dim pid = txtNewId.Text.Trim()
        Dim at = pid.LastIndexOf("@"c)
        If at <= 0 OrElse at = pid.Length - 1 Then
            MessageBox.Show(Me, "公開IDは「表示名@実効ID」の形式で入力してください。", Text)
            Return
        End If
        Dim name = New String(pid.Substring(0, at).Where(Function(ch) ch <> "@"c AndAlso Not Char.IsControl(ch)).ToArray()).Trim()
        Dim cid = pid.Substring(at + 1).Trim()
        If name.Length = 0 OrElse Not IsValidId(cid) Then
            MessageBox.Show(Me, "表示名または実効IDが正しくありません。", Text)
            Return
        End If

        ' IP:ポートは省略可能。省略するとホストに問い合わせて宛先を探す
        Dim endpoint = ""
        If txtNewAddr.Text.Trim().Length > 0 Then
            Dim ep As (Host As String, Port As Integer)
            Try
                ep = ParseEndpoint(txtNewAddr.Text)
            Catch ex As FormatException
                MessageBox.Show(Me, ex.Message, Text)
                Return
            End Try

            ' 自分自身の判定は「実効ID・このアプリの受信ポート・自分のPCのアドレス」が全部一致する場合のみ
            If cid = _selfId AndAlso ep.Port = _listenPort AndAlso IsLocalHost(ep.Host) Then
                MessageBox.Show(Me, "自分自身(このアプリの受信ポート)は登録できません。", Text)
                Return
            End If
            endpoint = $"{ep.Host}:{ep.Port}"
        End If

        Dim idx = _contacts.FindIndex(Function(x) x.Id = cid)
        Dim c As New Contact() With {.Name = name, .Id = cid, .Endpoint = endpoint, .Poll = chkPoll.Checked}
        If idx >= 0 Then
            _contacts(idx) = c
        Else
            _contacts.Add(c)
        End If
        _save()
        RefreshList()
        lst.SelectedItem = c
        ClearInputs()
    End Sub

    Private Sub OnDelete(sender As Object, e As EventArgs)
        Dim c = TryCast(lst.SelectedItem, Contact)
        If c Is Nothing Then Return
        _contacts.Remove(c)
        _save()
        RefreshList()
        ClearInputs()
    End Sub

    Private Shared Function IsLocalHost(host As String) As Boolean
        Try
            Dim addrs = Dns.GetHostAddresses(host)
            Dim locals = Dns.GetHostAddresses(Dns.GetHostName())
            Return addrs.Any(Function(a) IPAddress.IsLoopback(a) OrElse locals.Contains(a))
        Catch
            Return False
        End Try
    End Function

End Class
