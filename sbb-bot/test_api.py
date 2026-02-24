
import requests
import json
import urllib3

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

# Line 24K (AsisId=30 from map_asis.json or 27? Let's try 30)
# Step 804 in previous turn showed: "27": { "line_no": "24K", ..., "sbb_id": 30 }
# So AsisId=27.

url = "https://sbbpublicapi.sakarya.bel.tr/api/v1/VehicleTracking?AsisId=27"
headers = {
    "Origin": "https://ulasim.sakarya.bel.tr",
    "Referer": "https://ulasim.sakarya.bel.tr",
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
}

try:
    r = requests.get(url, headers=headers, verify=False, timeout=10)
    print(f"Status Code: {r.status_code}")
    print("Response Body:")
    try:
        data = r.json()
        print(json.dumps(data, indent=2, ensure_ascii=False))
    except:
        print(r.text)
except Exception as e:
    print(f"Error: {e}")
