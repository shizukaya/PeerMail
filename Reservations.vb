Option Strict On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Linq

''' <summary>
''' 送信予約(Outbox.dat)。相手が「自分宛のメッセージはありますか」と問い合わせてきたときに渡すために、
''' メッセージを保管しておく。渡せたものは予約から外れる。
''' </summary>
Public Class ReservationStore

    Private ReadOnly _path As String
    Private ReadOnly _items As New List(Of MailItem)
    Private ReadOnly _lock As New Object()

    Public Sub New(path As String)
        _path = path
        For Each m In MailStore.Load(path, True)
            m.IsReserved = True
            m.Folder = ""
            _items.Add(m)
        Next
    End Sub

    Public ReadOnly Property Count As Integer
        Get
            SyncLock _lock
                Return _items.Count
            End SyncLock
        End Get
    End Property

    ''' <summary>予約の一覧(古い順)のコピー</summary>
    Public Function Snapshot() As List(Of MailItem)
        SyncLock _lock
            Return _items.ToList()
        End SyncLock
    End Function

    ''' <summary>予約を1件追加して保存する。上限(max)に達していたら例外</summary>
    Public Function Add(peerId As String, text As String, max As Integer) As MailItem
        SyncLock _lock
            If _items.Count >= max Then
                Throw New InvalidOperationException($"予約が上限({max}件)に達しています。不要な予約を取り消してください。")
            End If
            Dim item As New MailItem() With {.Time = DateTime.Now, .PeerId = peerId, .Text = text, .IsSent = True, .IsReserved = True}
            _items.Add(item)
            Try
                SaveLocked()
            Catch
                _items.Remove(item)
                Throw
            End Try
            Return item
        End SyncLock
    End Function

    ''' <summary>peerId 宛ての予約を、古いものから最大 max 件返す(予約からは外さない)</summary>
    Public Function PeekFor(peerId As String, max As Integer) As List(Of MailItem)
        SyncLock _lock
            Return _items.Where(Function(m) m.PeerId = peerId).Take(max).ToList()
        End SyncLock
    End Function

    ''' <summary>予約から外して保存する(渡せたとき、または取り消したとき)。外せたら True</summary>
    Public Function Remove(item As MailItem) As Boolean
        SyncLock _lock
            If Not _items.Remove(item) Then Return False
            SaveLocked()
            Return True
        End SyncLock
    End Function

    ''' <summary>予約した日時から days 日を過ぎたものを取り除く(0なら何もしない)。取り除いた件数を返す</summary>
    Public Function ExpireOlderThan(days As Integer) As Integer
        If days <= 0 Then Return 0
        SyncLock _lock
            Dim limit = DateTime.Now.AddDays(-days)
            Dim removed = _items.RemoveAll(Function(m) m.Time < limit)
            If removed > 0 Then SaveLocked()
            Return removed
        End SyncLock
    End Function

    Private Sub SaveLocked()
        MailStore.Save(_path, _items)
    End Sub

End Class
