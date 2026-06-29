Attribute VB_Name = "RNAssistantWord"
Option Explicit

#If VBA7 Then
Private Declare PtrSafe Function SetDllDirectoryW Lib "kernel32" (ByVal lpPathName As LongPtr) As Long
Private Declare PtrSafe Function Host_ShowPanelEx Lib "RNAssistant.NativeHostCli.dll" (ByVal officeHwnd As LongPtr, ByVal rootPath As LongPtr, ByVal hostKind As Long) As Long
Private Declare PtrSafe Function Host_ClosePanel Lib "RNAssistant.NativeHostCli.dll" () As Long
Private Declare PtrSafe Function Host_SetPanelVisible Lib "RNAssistant.NativeHostCli.dll" (ByVal visible As Long) As Long
Private Declare PtrSafe Function Host_GetLastErrorMessage Lib "RNAssistant.NativeHostCli.dll" (ByVal buffer As LongPtr, ByVal bufferChars As Long) As Long
#End If

Public Sub RNAssistant_Open()
    ShowPanelCore
End Sub

Public Sub ShowAiPanel()
    ShowPanelCore
End Sub

Public Sub RNAssistant_Close()
    ReportHostResult Host_ClosePanel()
End Sub

Public Sub CloseAiPanel()
    RNAssistant_Close
End Sub

Public Sub RNAssistant_Hide()
    ReportHostResult Host_SetPanelVisible(0)
End Sub

Public Sub RNAssistant_RibbonOpen(ByVal control As IRibbonControl)
    ShowPanelCore
End Sub

Public Sub RNAssistant_RibbonClose(ByVal control As IRibbonControl)
    RNAssistant_Close
End Sub

Private Sub ShowPanelCore()
    On Error GoTo Failed
    Dim rootPath As String
    Dim officeHwnd As LongPtr
    rootPath = ResolvePortableRoot(ThisDocument.Path)
    officeHwnd = ActiveWindow.Hwnd
    If officeHwnd = 0 Then Err.Raise vbObjectError + 2000, , "Word HWND not found."
    PrepareDllFolder rootPath
    ReportHostResult Host_ShowPanelEx(officeHwnd, StrPtr(rootPath), 2)
    Exit Sub
Failed:
    MsgBox Err.Description, vbExclamation, "RN Assistant"
End Sub

Private Function ResolvePortableRoot(ByVal containerFolder As String) As String
    If DllExists(containerFolder) Then
        ResolvePortableRoot = containerFolder
    ElseIf DllExists(ParentFolder(containerFolder)) Then
        ResolvePortableRoot = ParentFolder(containerFolder)
    Else
        ResolvePortableRoot = containerFolder
    End If
End Function

Private Sub PrepareDllFolder(ByVal rootPath As String)
    If Len(rootPath) = 0 Then Err.Raise vbObjectError + 2001, , "Word template path is empty."
    If Not DllExists(rootPath) Then Err.Raise vbObjectError + 2002, , "RNAssistant.NativeHostCli.dll not found: " & rootPath
    If SetDllDirectoryW(StrPtr(rootPath)) = 0 Then Err.Raise vbObjectError + 2003, , "SetDllDirectoryW failed: " & rootPath
End Sub

Private Function DllExists(ByVal folder As String) As Boolean
    If Len(folder) > 0 Then DllExists = Len(Dir$(folder & "\RNAssistant.NativeHostCli.dll")) > 0
End Function

Private Function ParentFolder(ByVal folder As String) As String
    Dim p As Long
    p = InStrRev(folder, "\")
    If p > 1 Then ParentFolder = Left$(folder, p - 1)
End Function

Private Sub ReportHostResult(ByVal result As Long)
    If result <> 0 Then MsgBox "Native host error " & result & ":" & vbCrLf & LastHostError(), vbExclamation, "RN Assistant"
End Sub

Private Function LastHostError() As String
    Dim buffer As String
    Dim chars As Long
    buffer = String$(4096, vbNullChar)
    chars = Host_GetLastErrorMessage(StrPtr(buffer), Len(buffer))
    If chars > 0 Then LastHostError = Left$(buffer, chars)
End Function
