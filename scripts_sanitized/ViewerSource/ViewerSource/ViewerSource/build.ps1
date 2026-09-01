# Build script - concatenates everything into a single HTML file
# Place three.min.js and OrbitControls.js in the same folder

$three = Get-Content "three.min.js" -Raw
$orbit = Get-Content "OrbitControls.js" -Raw

$init = Get-Content "init.js" -Raw
$sectionCut = Get-Content "sectionCut.js" -Raw
$predicate = Get-Content "predicate.js" -Raw

$app = Get-Content "app.js" -Raw
$app_css = Get-Content "app.css" -Raw
$app_html = Get-Content "app.html" -Raw
$filePath = "C:/Temp/viewer.htm"

# $app_css (goes after body style)

@"
<!DOCTYPE html>
<html>
<body style="margin:0;background:#000">
<style>
$app_css
</style>
$app_html
<script>$three</script>
<script>$orbit</script>
<script>
$init
$sectionCut
$predicate
$app
</script>
</body>
</html>
"@ | Set-Content $filePath -Encoding UTF8

Write-Host "Built viewer.htm"
