' Kümes'i (C# sürümü) başlatır: çift tıkla. Zaten açıksa yeni kopya açılmaz, tavuk görünür hale gelir.
' Eski Electron sürümü için: Kumes-electron-baslat.vbs
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
exe = dir & "\cs\Kumes\bin\Release\net10.0-windows\Kumes.exe"
If Not fso.FileExists(exe) Then
	MsgBox "Kümes derlenmemiş. Önce şunu çalıştır:" & vbCrLf & "dotnet build cs\Kumes -c Release", 48, "Kümes"
	WScript.Quit 1
End If
sh.CurrentDirectory = fso.GetParentFolderName(exe)
sh.Run """" & exe & """", 1, False
