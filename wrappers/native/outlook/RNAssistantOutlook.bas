Attribute VB_Name = "RNAssistantOutlook"
Option Explicit

Public Sub RNAssistant_Open()
    LaunchRNAssistant ""
End Sub

Public Sub RNAssistant_Summarize()
    LaunchRNAssistant "summarize"
End Sub

Public Sub RNAssistant_ExplainSelection()
    LaunchRNAssistant "explain-selection"
End Sub

Public Sub RNAssistant_DraftRewrite()
    LaunchRNAssistant "draft-rewrite"
End Sub

Public Sub RNAssistant_RunSkill()
    LaunchRNAssistant "run-skill"
End Sub

Public Sub RNAssistant_Settings()
    LaunchRNAssistant "settings"
End Sub

Public Sub RNAssistant_Context()
    LaunchRNAssistant "context"
End Sub

Private Sub LaunchRNAssistant(ByVal actionName As String)
    Dim exePath As String
    exePath = Environ$("RNASSISTANT_DESKTOP_EXE")
    If Len(exePath) = 0 Then
        MsgBox "Set RNASSISTANT_DESKTOP_EXE to RNAssistant.Desktop.exe.", vbExclamation, "RN Assistant"
        Exit Sub
    End If

    Dim command As String
    command = QuoteArg(exePath) & " --host Outlook --hwnd " & CStr(CurrentHwnd()) & " --target-base64 " & QuoteArg(Base64Utf8(BuildTargetJson()))
    If Len(actionName) > 0 Then command = command & " --action " & QuoteArg(actionName)
    CreateObject("WScript.Shell").Run command, 1, False
End Sub

Private Function BuildTargetJson() As String
    Dim explorer As Explorer
    Set explorer = Application.ActiveExplorer

    If Not explorer Is Nothing Then
        If explorer.Selection.Count > 0 Then
            If TypeOf explorer.Selection.Item(1) Is MailItem Then
                Dim mail As MailItem
                Set mail = explorer.Selection.Item(1)
                BuildTargetJson = "{""Host"":""Outlook"",""Hwnd"":" & CStr(CurrentHwnd()) & ",""EntryId"":""" & JsonEscape(mail.EntryID) & """,""Name"":""" & JsonEscape(mail.Subject) & """}"
                Exit Function
            End If
        End If

        If Not explorer.CurrentFolder Is Nothing Then
            BuildTargetJson = "{""Host"":""Outlook"",""Hwnd"":" & CStr(CurrentHwnd()) & ",""FolderPath"":""" & JsonEscape(explorer.CurrentFolder.FolderPath) & """,""Name"":""" & JsonEscape(explorer.CurrentFolder.Name) & """}"
            Exit Function
        End If
    End If

    BuildTargetJson = "{""Host"":""Outlook"",""Hwnd"":" & CStr(CurrentHwnd()) & "}"
End Function

Private Function CurrentHwnd() As Long
    On Error Resume Next
    If Not Application.ActiveInspector Is Nothing Then
        CurrentHwnd = Application.ActiveInspector.HWND
        If CurrentHwnd <> 0 Then Exit Function
    End If
    If Not Application.ActiveExplorer Is Nothing Then
        CurrentHwnd = Application.ActiveExplorer.HWND
    End If
    On Error GoTo 0
End Function

Private Function QuoteArg(ByVal value As String) As String
    QuoteArg = Chr$(34) & Replace(value, Chr$(34), "\" & Chr$(34)) & Chr$(34)
End Function

Private Function JsonEscape(ByVal value As String) As String
    JsonEscape = Replace(Replace(value, "\", "\\"), Chr$(34), "\" & Chr$(34))
End Function

Private Function Base64Utf8(ByVal value As String) As String
    Dim stream As Object
    Set stream = CreateObject("ADODB.Stream")
    stream.Type = 2
    stream.Charset = "utf-8"
    stream.Open
    stream.WriteText value
    stream.Position = 0
    stream.Type = 1

    Dim bytes As Variant
    bytes = stream.Read
    stream.Close

    Dim xml As Object
    Set xml = CreateObject("MSXML2.DOMDocument.6.0")
    Dim node As Object
    Set node = xml.createElement("b64")
    node.DataType = "bin.base64"
    node.nodeTypedValue = bytes
    Base64Utf8 = Replace(Replace(node.Text, vbCr, ""), vbLf, "")
End Function
