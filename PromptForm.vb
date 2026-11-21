Option Strict On
Option Infer On

Imports System
Imports System.Drawing
Imports System.Windows.Forms

''' <summary>1行の文字列を入力してもらう小さなダイアログ(フォルダ名の入力用)</summary>
Public Class PromptForm
    Inherits Form

    Private ReadOnly lblPrompt As New Label() With {.Dock = DockStyle.Top, .Height = 30, .TextAlign = ContentAlignment.MiddleLeft}
    Private ReadOnly txtInput As New TextBox() With {.Dock = DockStyle.Top}
    Private ReadOnly btnOk As New Button() With {.Text = "OK", .Width = 90, .Height = 30, .DialogResult = DialogResult.OK}
    Private ReadOnly btnCancel As New Button() With {.Text = "キャンセル", .Width = 90, .Height = 30, .DialogResult = DialogResult.Cancel}

    ''' <summary>入力された文字列を返す。キャンセルされたら Nothing</summary>
    Public Shared Function Ask(owner As IWin32Window, title As String, prompt As String, initial As String) As String
        Using f As New PromptForm(title, prompt, initial)
            If f.ShowDialog(owner) = DialogResult.OK Then Return f.txtInput.Text
        End Using
        Return Nothing
    End Function

    Private Sub New(title As String, prompt As String, initial As String)
        Text = title
        Font = New Font("Yu Gothic UI", 10.0F)
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False
        MinimizeBox = False
        ShowInTaskbar = False
        StartPosition = FormStartPosition.CenterParent
        ClientSize = New Size(420, 140)
        Padding = New Padding(10)

        lblPrompt.Text = prompt
        txtInput.Text = initial

        Dim flow As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .Height = 44, .FlowDirection = FlowDirection.RightToLeft, .WrapContents = False}
        flow.Controls.Add(btnCancel)
        flow.Controls.Add(btnOk)

        Controls.Add(flow)
        Controls.Add(txtInput)     ' 上に積むものは、下にあるものから先に追加する
        Controls.Add(lblPrompt)

        AcceptButton = btnOk
        CancelButton = btnCancel
    End Sub

    Protected Overrides Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)
        txtInput.SelectAll()
        txtInput.Focus()
    End Sub

End Class
