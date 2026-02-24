import requests
import concurrent.futures
import urllib3
import sys

base_url = "https://sbbpublicapi.sakarya.bel.tr"
headers = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    "Referer": "https://ulasim.sakarya.bel.tr",
    "Origin": "https://ulasim.sakarya.bel.tr"
}

# Common words to test as endpoints under api/v1/ and api/v1/Ulasim/
words = [
    "Vehicle", "Vehicles", "Arac", "Araclar", "Bus", "Buses", "Otobus", "Otobusler",
    "Stop", "Stops", "Durak", "Duraklar", "Station", "Stations", "Istasyon",
    "Route", "Routes", "Guzergah", "Hat", "Hatlar", "Line", "Lines",
    "Schedule", "Schedules", "TimeTable", "Sefer", "Seferler", "Saatler",
    "Tariff", "Tariffs", "Fare", "Fares", "Ucret", "Fiyat", "Ticket", "Bilet",
    "Live", "Realtime", "Location", "Locations", "Konum", "Pozisyon",
    "News", "Haber", "Haberler", "Announcement", "Announcements", "Duyuru", "Duyurular",
    "Config", "Configuration", "Settings", "Ayar", "Ayarlar", "App", "Mobile",
    "Lookup", "Types", "Definitions", "Tanimlar", "Enum", "Enums",
    "City", "District", "Neighborhood", "Ilce", "Mahalle",
    "System", "Status", "Health", "Version", "Info",
    "SmartStop", "AkilliDurak", "Qr", "Kiosk",
    "Card", "Kart", "Balance", "Bakiye", "Dolum",
    "Search", "Arama", "Find", "Bul",
    "Smart", "Akilli", "Public", "Common", "General"
]

paths_to_check = [
    # Swagger/Docs
    "/swagger/index.html",
    "/swagger/v1/swagger.json",
    
    # Root level checks
    "/api/v1/Ulasim",
    "/api/v1/Public",
    "/api/v1/General",
    "/api/v1/Common",
]

# Generate combinations
for w in words:
    # Check Controller/Resource style: /api/v1/Vehicle
    paths_to_check.append(f"/api/v1/{w}")
    # Check under Ulasim: /api/v1/Ulasim/Vehicle
    paths_to_check.append(f"/api/v1/Ulasim/{w}")
    paths_to_check.append(f"/api/v1/Ulasim/{w.lower()}")
    # Plural/Singular variations are handled by the list but let's ensure lowercase too
    paths_to_check.append(f"/api/v1/{w.lower()}")

# Specific known patterns to verify/fuzz
paths_to_check.extend([
    "/api/v1/Ulasim/GetLines",
    "/api/v1/Ulasim/GetRoutes",
    "/api/v1/Ulasim/GetStops",
    "/api/v1/Ulasim/GetVehicles",
    "/api/v1/Ulasim/InstantVehicles",
    "/api/v1/Ulasim/LiveMap",
    "/api/v1/Ulasim/AracKonumlari",
    "/api/v1/Ulasim/HatListesi",
    # Based on existing patterns
    "/api/v1/Ulasim/stops",
    "/api/v1/Ulasim/routes",
])

def check_url(path):
    url = f"{base_url}{path}"
    try:
        response = requests.get(url, headers=headers, timeout=3, verify=False)
        return (path, response.status_code, response.headers.get('Content-Type', ''))
    except Exception as e:
        return (path, "Error", str(e))

print(f"Scanning {len(paths_to_check)} endpoints on {base_url}...")
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

with concurrent.futures.ThreadPoolExecutor(max_workers=10) as executor:
    # Submit all
    future_to_url = {executor.submit(check_url, path): path for path in paths_to_check}
    
    for future in concurrent.futures.as_completed(future_to_url):
        path = future_to_url[future]
        try:
            p, status, ctype = future.result()
            if status != 404 and status != "Error":
                print(f"[{status}] {path} ({ctype})")
            else:
                pass
        except Exception as exc:
            pass
        sys.stdout.flush()

print("Scan complete.")
