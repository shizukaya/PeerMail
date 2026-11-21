Option Strict On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Threading.Tasks
Imports System.Windows.Forms

''' <summary>
''' 新規メッセージ / 返信の作成画面。「送信」はすぐに相手へ送る(失敗したら閉じずにエラーを表示)。
''' 「予約として保存」は、相手が自分宛のメッセージを問い合わせてきたときに渡すために保管しておく。
''' </summary>
Public Class ComposeForm
    Inherits Form

    Private ReadOnly cmbTo As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Dock = DockStyle.Fill}
    Private ReadOnly txtBody As New TextBox() With {.Multiline = True, .ScrollBars = ScrollBars.Vertical, .AcceptsReturn = True, .Dock = DockStyle.Fill}
    Private ReadOnly btnSend As New Button() With {.Text = "送信 (Ctrl+Enter)", .AutoSize = True}
    Private ReadOnly btnReserve As New Button() With {.Text = "予約として保存", .AutoSize = True}
    Private ReadOnly btnCancel As New Button() With {.Text = "閉じる", .AutoSize = True, .DialogResult = DialogResult.Cancel}
    Private ReadOnly lblError As New Label() With {.AutoSize = False, .Dock = DockStyle.Fill, .TextAlign = ContentAlignment.MiddleLeft}
    Private ReadOnly _send As Func(Of Contact, String, Task)
    Private ReadOnly _reserve As Action(Of Contact, String)
    Private _sending As Boolean

    Public Sub New(contacts As IEnumerable(Of Contact), selected As Contact, send As Func(Of Contact, String, Task), reserve As Action(Of Contact, String))
        Text = "新規メッセージ"
        Font = New Font("Yu Gothic UI", 10.0F)
        ClientSize = New Size(780, 420)
        MinimumSize = New Size(560, 320)
        StartPosition = FormStartPosition.CenterParent
        Padding = New Padding(8)
        _send = send
        _reserve = reserve

        For Each c In contacts
            cmbTo.Items.Add(c)
        Next
        ' 宛先リストを開いたとき、IDが長くても全体が見えるように、最も長い項目に合わせて幅を広げる
        Dim widest = 0
        For Each o In cmbTo.Items
            widest = Math.Max(widest, TextRenderer.MeasureText(o.ToString(), Font).Width)
        Next
        cmbTo.DropDownWidth = widest + SystemInformation.VerticalScrollBarWidth + 8

        If selected IsNot Nothing Then
            cmbTo.SelectedItem = selected
        ElseIf cmbTo.Items.Count > 0 Then
            cmbTo.SelectedIndex = 0
        End If

        Dim pnlTo As New Panel() With {.Dock = DockStyle.Top, .Height = 38}
        pnlTo.Controls.Add(cmbTo)     ' Fill は最初に追加する
        pnlTo.Controls.Add(New Label() With {.Text = "宛先", .Dock = DockStyle.Left, .Width = 60, .TextAlign = ContentAlignment.MiddleLeft})

        Dim flow As New FlowLayoutPanel() With {.Dock = DockStyle.Right, .AutoSize = True, .WrapContents = False, .FlowDirection = FlowDirection.RightToLeft}
        flow.Controls.Add(btnCancel)      ' 右から順に: 閉じる / 送信 / 予約として保存
        flow.Controls.Add(btnSend)
        flow.Controls.Add(btnReserve)
        Dim pnlBottom As New Panel() With {.Dock = DockStyle.Bottom, .Height = 48}
        pnlBottom.Controls.Add(lblError)
        pnlBottom.Controls.Add(flow)

        Controls.Add(txtBody)
        Controls.Add(pnlBottom)
        Controls.Add(pnlTo)

        CancelButton = btnCancel
        AddHandler btnSend.Click, AddressOf OnSendClick
        AddHandler btnReserve.Click, AddressOf OnReserveClick
        AddHandler txtBody.KeyDown, AddressOf OnBodyKeyDown
    End Sub

    Protected Overrides Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)
        txtBody.Focus()
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        If _sending Then e.Cancel = True      ' 送信中は閉じない
        MyBase.OnFormClosing(e)
    End Sub

    Private Sub OnBodyKeyDown(sender As Object, e As KeyEventArgs)
        If e.Control AndAlso e.KeyCode = Keys.Enter Then
            e.SuppressKeyPress = True
            If btnSend.Enabled Then OnSendClick(Nothing, EventArgs.Empty)
        End If
    End Sub

    Private Async Sub OnSendClick(sender As Object, e As EventArgs)
        Dim c = TryCast(cmbTo.SelectedItem, Contact)
        If c Is Nothing Then
            ShowError("宛先を選択してください。")
            Return
        End If
        Dim msg = txtBody.Text.Trim()
        If msg.Length = 0 Then
            ShowError("本文を入力してください。")
            Return
        End If

        _sending = True
        btnSend.Enabled = False
        btnReserve.Enabled = False
        cmbTo.Enabled = False
        txtBody.ReadOnly = True
        lblError.ForeColor = SystemColors.GrayText
        lblError.Text = "送信中です…"
        Try
            Await _send(c, msg)
            _sending = False
            DialogResult = DialogResult.OK      ' 送信成功: 画面を閉じる
        Catch ex As Exception
            _sending = False
            btnSend.Enabled = True
            btnReserve.Enabled = True
            cmbTo.Enabled = True
            txtBody.ReadOnly = False
            ShowError("送信できませんでした: " & ex.Message & "  (「予約として保存」を押すと、相手が問い合わせてきたときに送れます)")
        End Try
    End Sub

    ''' <summary>「予約として保存」: 今は送らず、相手が問い合わせてきたときに渡すために保管する</summary>
    Private Sub OnReserveClick(sender As Object, e As EventArgs)
        Dim c = TryCast(cmbTo.SelectedItem, Contact)
        If c Is Nothing Then
            ShowError("宛先を選択してください。")
            Return
        End If
        Dim msg = txtBody.Text.Trim()
        If msg.Length = 0 Then
            ShowError("本文を入力してください。")
            Return
        End If
        Try
            _reserve(c, msg)
            DialogResult = DialogResult.OK      ' 予約できた: 画面を閉じる
        Catch ex As Exception
            ShowError("予約できませんでした: " & ex.Message)
        End Try
    End Sub

    Private Sub ShowError(message As String)
        lblError.ForeColor = Color.Firebrick
        lblError.Text = message
    End Sub

End Class
