# Windows equivalent of seed.sh. Usage: ./scripts/seed.ps1
$ErrorActionPreference = "Stop"
$catalog = if ($env:CATALOG) { $env:CATALOG } else { "http://localhost:5101" }
$h = @{ "X-User-Id" = "admin-1"; "X-User-Role" = "admin" }
function Post($path, $body) { Invoke-RestMethod -Method Post -Uri "$catalog$path" -Headers $h -ContentType "application/json" -Body ($body | ConvertTo-Json -Depth 5) }

$venue = (Post "/admin/venues" @{ name = "Riverside Arena"; city = "Pune"; timezone = "Asia/Kolkata" }).venueId
$hall  = (Post "/admin/venues/$venue/halls" @{ name = "Hall 1"; grid = @{ rowCount = 10; seatsPerRow = 20; defaultCategory = "SILVER";
          categoryByRow = @{ A = "RECLINER"; B = "RECLINER"; C = "GOLD"; D = "GOLD"; E = "GOLD" } } }).hallId
$event = (Post "/admin/events" @{ title = "Midnight Echoes Live"; category = "CONCERT"; language = "en"; durationMin = 150 }).eventId
$show  = (Post "/admin/shows" @{ eventId = $event; hallId = $hall; startsAt = (Get-Date).ToUniversalTime().AddDays(7).ToString("yyyy-MM-ddT19:30:00Z");
          saleOpensAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"); currency = "INR";
          prices = @{ RECLINER = 90000; GOLD = 50000; SILVER = 30000 } }).showId
Invoke-RestMethod -Method Post -Uri "$catalog/admin/shows/$show/publish" -Headers $h | Out-Null

Write-Host "Published show: $show"
Start-Sleep -Seconds 3
Write-Host "Open http://localhost:5105 and paste the show id."
