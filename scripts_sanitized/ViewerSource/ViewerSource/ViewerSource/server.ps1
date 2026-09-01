# $listener = [System.Net.HttpListener]::new()
# $listener.Prefixes.Add("http://localhost:8080/")
# $listener.Start()
# Write-Host "Serving at http://localhost:8080/"
# while ($listener.IsListening) 
# {
	# $ctx = $listener.GetContext()
	# $path = Join-Path "C:\Temp" ($ctx.Request.Url.LocalPath.TrimStart('/'))
	# if (Test-Path $path)
	# {
		# $bytes = [IO.File]::ReadAllBytes($path)
		# $ctx.Response.ContentLength64 = $bytes.Length
		# $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
	# } else {
		# $ctx.Response.StatusCode = 404
	# }
	# $ctx.Response.Close()
# }

& "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" --allow-file-access-from-files --user-data-dir="C:\Temp\edge-debug" "file:///C:/Temp/viewer.htm"