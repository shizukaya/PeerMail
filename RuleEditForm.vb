Option Strict On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Windows.Forms

''' <summary>振り分けルール1件の作成・編集。OKを押すと、渡された MailRule に内容を書き戻す</summary>
Public Class RuleEditForm
    Inherits Form

    Private ReadOnly _rule As MailRule
    Private ReadOnly txtName As New TextBox()
    Private ReadOnly chkEnabled As New CheckBox() With {.Text = "このルールを使う", .AutoSize = True, .Checked = True}
    Private ReadOnly cmbKind As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly cmbSender As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly txtSenderName As New TextBox()
    Private ReadOnly txtBody As New TextBox()
    Private ReadOnly cmbTarget As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly btnOk As New Button() With {.Text = "OK", .Width = 100, .Height = 32}
    Private ReadOnly btnCancel As New Button() With {.Text = "キャンセル", .Width = 100, .Height = 32, .DialogResult = DialogResult.Cancel}
    Private ReadOnly _note As New Label() With {.AutoSize = True}      ' 折り返しに合わせて、高さが自動で決まる
    Private _row As Integer

    Public Sub New(rule As MailRule, contacts As List(Of Contact), targets As List(Of FolderItem))
        Text = "振り分けルール"
        Font = New Font("Yu Gothic UI", 10.0F)
        ClientSize = New Size(820, 580)
        MinimumSize = New Size(700, 520)
        StartPosition = FormStartPosition.CenterParent
        Padding = New Padding(10)
        _rule = rule

        cmbKind.Items.AddRange(New Object() {"指定しない", "連絡先に登録済みの人", "連絡先に未登録の人"})
        cmbSender.Items.Add("(指定しない)")
        For Each c In contacts
            cmbSender.Items.Add(c)
        Next
        ' ルールが指している連絡先が削除されていても、選択を保てるようにする
        If rule.SenderId.Length > 0 AndAlso Not contacts.Any(Function(c) c.Id = rule.SenderId) Then
            cmbSender.Items.Add(New Contact() With {.Name = "(削除された連絡先)", .Id = rule.SenderId})
        End If
        For Each t In targets
            cmbTarget.Items.Add(t)
        Next

        ' 画面全体を3段の表にする: 上=入力欄(余った高さを使い、足りなければスクロール) / 中=説明文 / 下=ボタン。
        ' 説明文とボタンは、内容に合わせた高さで、必ず画面内に表示される。
        Dim tbl As New TableLayoutPanel() With {.Dock = DockStyle.Fill, .ColumnCount = 2, .AutoScroll = True}
        tbl.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 230))
        tbl.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        AddRow(tbl, "ルール名", txtName)
        AddRow(tbl, "", chkEnabled)
        AddHeading(tbl, "【条件】 指定したものを、すべて満たしたメッセージが対象です")
        AddRow(tbl, "差出人の種類", cmbKind)
        AddRow(tbl, "特定の連絡先から", cmbSender)
        AddRow(tbl, "差出人の表示名に含む文字", txtSenderName)
        AddRow(tbl, "本文に含む文字", txtBody)
        AddHeading(tbl, "【移動先】")
        AddRow(tbl, "移動先のフォルダ", cmbTarget)

        _note.Text = "大文字・小文字は区別しません。連絡先に未登録の人の表示名は、相手が自分で名乗っているものなので、他人になりすませます。" &
                     "確実に特定したい相手は「特定の連絡先から」で指定してください。"
        _note.Margin = New Padding(3, 10, 3, 10)
        UpdateNoteWidth()

        Dim flow As New FlowLayoutPanel() With {.Dock = DockStyle.Fill, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .FlowDirection = FlowDirection.RightToLeft, .WrapContents = False}
        flow.Controls.Add(btnCancel)
        flow.Controls.Add(btnOk)

        Dim root As New TableLayoutPanel() With {.Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 3}
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        root.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        root.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        root.Controls.Add(tbl, 0, 0)
        root.Controls.Add(_note, 0, 1)
        root.Controls.Add(flow, 0, 2)
        Controls.Add(root)
        AddHandler SizeChanged, Sub(s, e) UpdateNoteWidth()

        ' 入力欄に、いまの内容を入れる
        txtName.Text = rule.Name
        chkEnabled.Checked = rule.Enabled
        cmbKind.SelectedIndex = rule.SenderKind
        cmbSender.SelectedIndex = 0
        If rule.SenderId.Length > 0 Then
            For i = 1 To cmbSender.Items.Count - 1
                If DirectCast(cmbSender.Items(i), Contact).Id = rule.SenderId Then cmbSender.SelectedIndex = i
            Next
        End If
        txtSenderName.Text = rule.SenderNameContains
        txtBody.Text = rule.BodyContains
        For i = 0 To cmbTarget.Items.Count - 1
            If DirectCast(cmbTarget.Items(i), FolderItem).Id = rule.TargetFolderId Then cmbTarget.SelectedIndex = i
        Next

        AcceptButton = btnOk
        CancelButton = btnCancel
        AddHandler btnOk.Click, AddressOf OnOk
    End Sub

    ''' <summary>2列にまたがる見出しの行</summary>
    Private Sub AddHeading(tbl As TableLayoutPanel, caption As String)
        Dim r = _row
        _row += 1
        tbl.RowCount = _row
        tbl.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        Dim lbl As New Label() With {.Text = caption, .AutoSize = True, .Margin = New Padding(3, 14, 3, 4), .Font = New Font(Font, FontStyle.Bold)}
        tbl.Controls.Add(lbl, 0, r)
        tbl.SetColumnSpan(lbl, 2)
    End Sub

    ''' <summary>説明文を、画面の幅で折り返す(幅が変わったら更新する)</summary>
    Private Sub UpdateNoteWidth()
        _note.MaximumSize = New Size(Math.Max(200, ClientSize.Width - Padding.Horizontal - 10), 0)
    End Sub

    Private Sub AddRow(tbl As TableLayoutPanel, caption As String, ctl As Control)
        Dim r = _row
        _row += 1
        tbl.RowCount = _row
        tbl.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        tbl.Controls.Add(New Label() With {.Text = caption, .AutoSize = True, .Anchor = AnchorStyles.Left, .Margin = New Padding(3, 8, 3, 8)}, 0, r)
        ctl.Anchor = AnchorStyles.Left Or AnchorStyles.Right
        ctl.Margin = New Padding(3, 5, 3, 5)
        tbl.Controls.Add(ctl, 1, r)
    End Sub

    Private Shared Function Clean(s As String) As String
        Dim t = New String(s.Where(Function(ch) Not Char.IsControl(ch)).ToArray()).Trim()
        Return If(t.Length > 100, t.Substring(0, 100), t)
    End Function

    Private Sub OnOk(sender As Object, e As EventArgs)
        Dim kind = Math.Max(0, cmbKind.SelectedIndex)
        Dim ct = TryCast(cmbSender.SelectedItem, Contact)
        Dim senderId = If(ct IsNot Nothing, ct.Id, "")
        Dim senderName = Clean(txtSenderName.Text)
        Dim body = Clean(txtBody.Text)
        Dim target = TryCast(cmbTarget.SelectedItem, FolderItem)

        If kind = 0 AndAlso senderId.Length = 0 AndAlso senderName.Length = 0 AndAlso body.Length = 0 Then
            MessageBox.Show(Me, "条件を1つ以上指定してください。", Text)
            Return
        End If
        If target Is Nothing Then
            MessageBox.Show(Me, "移動先のフォルダを選択してください。", Text)
            Return
        End If

        Dim name = Clean(txtName.Text)
        If name.Length = 0 Then name = "新しいルール"
        _rule.Name = name
        _rule.Enabled = chkEnabled.Checked
        _rule.SenderKind = kind
        _rule.SenderId = senderId
        _rule.SenderNameContains = senderName
        _rule.BodyContains = body
        _rule.TargetFolderId = target.Id
        DialogResult = DialogResult.OK
    End Sub

End Class
