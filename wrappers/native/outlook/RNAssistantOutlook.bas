Attribute VB_Name = "RNAssistantOutlook"
Option Explicit

#If VBA7 Then
Private Declare PtrSafe Function SetDllDirectoryW Lib "kernel32" (ByVal lpPathName As LongPtr) As Long
Private Declare PtrSafe Function FindWindowW Lib "user32" (ByVal lpClassName As LongPtr, ByVal lpWindowName As LongPtr) As LongPtr
Private Declare PtrSafe Function Host_ShowPanelEx Lib "RNAssistant.NativeHostCli.dll" (ByVal officeHwnd As LongPtr, ByVal rootPath As LongPtr, ByVal hostKind As Long) As Long
Private Declare PtrSafe Function Host_ClosePanel Lib "RNAssistant.NativeHostCli.dll" () As Long
Private Declare PtrSafe Function Host_SetPanelVisible Lib "RNAssistant.NativeHostCli.dll" (ByVal visible As Long) As Long
Private Declare PtrSafe Function Host_GetLastErrorMessage Lib "RNAssistant.NativeHostCli.dll" (ByVal buffer As LongPtr, ByVal bufferChars As Long) As Long
#End If

Public Sub RNAssistant_Open()
    On Error GoTo Failed
    Dim rootPath As String
    Dim officeHwnd As LongPtr
    rootPath = GetRootPath()
    officeHwnd = CurrentHwnd()
    If officeHwnd = 0 Then Err.Raise vbObjectError + 4000, , "Outlook HWND not found."
    PrepareDllFolder rootPath
    ReportHostResult Host_ShowPanelEx(officeHwnd, StrPtr(rootPath), 4)
    Exit Sub
Failed:
    MsgBox Err.Description, vbExclamation, "RN Assistant"
End Sub

Public Sub ShowAiPanel()
    RNAssistant_Open
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

Private Function GetRootPath() As String
    GetRootPath = Environ$("RNASSISTANT_ROOT")
    If Len(GetRootPath) = 0 Then GetRootPath = "C:\Temp\RNAssistant"
End Function

Private Function CurrentHwnd() As LongPtr
    On Error Resume Next
    If Not Application.ActiveInspector Is Nothing Then CurrentHwnd = Application.ActiveInspector.HWND
    If CurrentHwnd = 0 And Not Application.ActiveExplorer Is Nothing Then CurrentHwnd = Application.ActiveExplorer.HWND
    On Error GoTo 0
    If CurrentHwnd = 0 Then CurrentHwnd = FindWindowW(StrPtr("rctrl_renwnd32"), 0)
End Function

Private Sub PrepareDllFolder(ByVal rootPath As String)
    If Len(Dir$(rootPath & "\RNAssistant.NativeHostCli.dll")) = 0 Then Err.Raise vbObjectError + 4001, , "RNAssistant.NativeHostCli.dll not found: " & rootPath
    If SetDllDirectoryW(StrPtr(rootPath)) = 0 Then Err.Raise vbObjectError + 4002, , "SetDllDirectoryW failed: " & rootPath
End Sub

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
