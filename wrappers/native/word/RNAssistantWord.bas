Attribute VB_Name = "RNAssistantWord"
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
    command = QuoteArg(exePath) & " --host Word --hwnd " & CStr(CurrentHwnd()) & " --target-base64 " & QuoteArg(Base64Utf8(BuildTargetJson()))
    If Len(actionName) > 0 Then command = command & " --action " & QuoteArg(actionName)
    CreateObject("WScript.Shell").Run command, 1, False
End Sub

Private Function BuildTargetJson() As String
    If Documents.Count = 0 Then
        BuildTargetJson = "{""Host"":""Word"",""Hwnd"":" & CStr(CurrentHwnd()) & "}"
        Exit Function
    End If

    Dim doc As Document
    Set doc = ActiveDocument
    BuildTargetJson = "{""Host"":""Word"",""Hwnd"":" & CStr(CurrentHwnd()) & ",""FullName"":""" & JsonEscape(doc.FullName) & """,""Path"":""" & JsonEscape(doc.FullName) & """,""Name"":""" & JsonEscape(doc.Name) & """,""Selection"":""" & JsonEscape(CStr(Selection.Start) & ":" & CStr(Selection.End)) & """}"
End Function

Private Function CurrentHwnd() As Long
    On Error Resume Next
    CurrentHwnd = Application.ActiveWindow.Hwnd
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
