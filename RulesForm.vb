Option Strict On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Windows.Forms

''' <summary>振り分けルールの一覧。追加・編集・削除・順番の入れ替え・受信トレイへの今すぐ適用ができる</summary>
Public Class RulesForm
    Inherits Form

    Private ReadOnly _rules As List(Of MailRule)
    Private ReadOnly _targets As List(Of FolderItem)
    Private ReadOnly _contacts As List(Of Contact)
    Private ReadOnly _save As Action
    Private ReadOnly _applyNow As Action

    Private ReadOnly lst As New ListBox() With {.Dock = DockStyle.Fill, .IntegralHeight = False, .HorizontalScrollbar = True}
    Private ReadOnly btnAdd As New Button() With {.Text = "追加...", .AutoSize = True}
    Private ReadOnly btnEdit As New Button() With {.Text = "編集...", .AutoSize = True}
    Private ReadOnly btnDelete As New Button() With {.Text = "削除", .AutoSize = True}
    Private ReadOnly btnUp As New Button() With {.Text = "上へ", .AutoSize = True}
    Private ReadOnly btnDown As New Button() With {.Text = "下へ", .AutoSize = True}
    Private ReadOnly btnApply As New Button() With {.Text = "受信トレイに今すぐ適用...", .AutoSize = True}
    Private ReadOnly btnClose As New Button() With {.Text = "閉じる", .AutoSize = True, .DialogResult = DialogResult.OK}

    ''' <param name="targets">移動先に選べるフォルダ(ユーザーフォルダと ごみ箱)</param>
    ''' <param name="save">ルールを変更したときに、保存する処理</param>
    ''' <param name="applyNow">「受信トレイに今すぐ適用」の処理</param>
    Public Sub New(rules As List(Of MailRule), targets As List(Of FolderItem), contacts As List(Of Contact), save As Action, applyNow As Action)
        Text = "振り分けルール"
        Font = New Font("Yu Gothic UI", 10.0F)
        ClientSize = New Size(900, 460)
        MinimumSize = New Size(700, 380)
        StartPosition = FormStartPosition.CenterParent
        Padding = New Padding(10)
        _rules = rules
        _targets = targets
        _contacts = contacts
        _save = save
        _applyNow = applyNow

        Dim note As New Label() With {.Dock = DockStyle.Top, .Height = 44,
            .Text = "受信したメッセージを、上のルールから順に調べ、最初に条件を満たしたルールの移動先のフォルダへ、自動的に移動します。"}
        Dim buttons As New FlowLayoutPanel() With {.Dock = DockStyle.Right, .AutoSize = True, .FlowDirection = FlowDirection.TopDown, .WrapContents = False}
        buttons.Controls.AddRange(New Control() {btnAdd, btnEdit, btnDelete, btnUp, btnDown, btnApply, btnClose})

        Controls.Add(lst)         ' Fill は最初に追加する
        Controls.Add(buttons)
        Controls.Add(note)

        CancelButton = btnClose
        AddHandler btnAdd.Click, AddressOf OnAdd
        AddHandler btnEdit.Click, AddressOf OnEdit
        AddHandler lst.DoubleClick, AddressOf OnEdit
        AddHandler btnDelete.Click, AddressOf OnDelete
        AddHandler btnUp.Click, Sub(s, e) MoveRule(-1)
        AddHandler btnDown.Click, Sub(s, e) MoveRule(1)
        AddHandler btnApply.Click, Sub(s, e) _applyNow()
        RefreshList(-1)
    End Sub

    Private Function Describe(r As MailRule) As String
        Dim conds As New List(Of String)
        If r.SenderKind = 1 Then conds.Add("連絡先に登録済みの人")
        If r.SenderKind = 2 Then conds.Add("連絡先に未登録の人")
        If r.SenderId.Length > 0 Then
            Dim c = _contacts.FirstOrDefault(Function(x) x.Id = r.SenderId)
            conds.Add("差出人が「" & If(c IsNot Nothing, c.Name, "(削除された連絡先)") & "」")
        End If
        If r.SenderNameContains.Length > 0 Then conds.Add($"差出人名に「{r.SenderNameContains}」")
        If r.BodyContains.Length > 0 Then conds.Add($"本文に「{r.BodyContains}」")
        Dim target = _targets.FirstOrDefault(Function(f) f.Id = r.TargetFolderId)
        Dim dest = If(target IsNot Nothing, target.Name, "(移動先なし)")
        Return $"{If(r.Enabled, "", "[無効] ")}{r.Name}  ―  {String.Join(" かつ ", conds)}  →  {dest}"
    End Function

    Private Sub RefreshList(select_ As Integer)
        lst.Items.Clear()
        For Each r In _rules
            lst.Items.Add(Describe(r))
        Next
        If select_ >= 0 AndAlso select_ < lst.Items.Count Then lst.SelectedIndex = select_
    End Sub

    Private Sub OnAdd(sender As Object, e As EventArgs)
        Dim r As New MailRule()
        Using f As New RuleEditForm(r, _contacts, _targets)
            If f.ShowDialog(Me) <> DialogResult.OK Then Return
        End Using
        _rules.Add(r)
        _save()
        RefreshList(_rules.Count - 1)
    End Sub

    Private Sub OnEdit(sender As Object, e As EventArgs)
        Dim idx = lst.SelectedIndex
        If idx < 0 Then Return
        Dim work = _rules(idx).Copy()
        Using f As New RuleEditForm(work, _contacts, _targets)
            If f.ShowDialog(Me) <> DialogResult.OK Then Return
        End Using
        _rules(idx) = work
        _save()
        RefreshList(idx)
    End Sub

    Private Sub OnDelete(sender As Object, e As EventArgs)
        Dim idx = lst.SelectedIndex
        If idx < 0 Then Return
        If MessageBox.Show(Me, $"ルール「{_rules(idx).Name}」を削除しますか?(すでに移動したメッセージは、そのままです)", Text,
                           MessageBoxButtons.YesNo, MessageBoxIcon.Question) <> DialogResult.Yes Then Return
        _rules.RemoveAt(idx)
        _save()
        RefreshList(Math.Min(idx, _rules.Count - 1))
    End Sub

    ''' <summary>ルールの順番を入れ替える(上にあるルールほど優先される)</summary>
    Private Sub MoveRule(delta As Integer)
        Dim idx = lst.SelectedIndex
        Dim dest = idx + delta
        If idx < 0 OrElse dest < 0 OrElse dest >= _rules.Count Then Return
        Dim r = _rules(idx)
        _rules.RemoveAt(idx)
        _rules.Insert(dest, r)
        _save()
        RefreshList(dest)
    End Sub

End Class
