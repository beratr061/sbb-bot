import requests
import json
import urllib3
import time

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

base_url = "https://sbbpublicapi.sakarya.bel.tr/api/v1/VehicleTracking?AsisId={}"
headers = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    "Origin": "https://ulasim.sakarya.bel.tr",
    "Referer": "https://ulasim.sakarya.bel.tr"
}

print("Mapping AsisId to LineNumber (Scanning 1-400)...")

mapping = {}
count = 0

for asis_id in range(1, 401):    
    url = base_url.format(asis_id)
    try:
        r = requests.get(url, headers=headers, verify=False, timeout=5)
        if r.status_code == 200:
            data = r.json()
            if data and isinstance(data, list) and len(data) > 0:
                first = data[0]
                line_no = first.get("lineNumber")
                line_name = first.get("lineName")
                sbb_id = first.get("lineId")
                
                print(f"[MATCH] Asis: {asis_id} -> No: {line_no} ({line_name}) [Internal: {sbb_id}]")
                
                mapping[asis_id] = {
                    "line_no": line_no,
                    "name": line_name,
                    "sbb_id": sbb_id
                }
                count += 1
    except Exception as e:
        # print(f"Error {asis_id}: {e}")
        pass
    
    if asis_id % 50 == 0:
        print(f"Progress: {asis_id}/400")

print(f"Total Matches: {count}")
with open("asis_map.json", "w", encoding="utf-8") as f:
    json.dump(mapping, f, indent=2, ensure_ascii=False)
