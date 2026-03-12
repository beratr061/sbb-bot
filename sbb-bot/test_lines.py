import requests
import json
import urllib3
urllib3.disable_warnings()

url = "https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim?busType=5731"
headers = {
    "Origin": "https://ulasim.sakarya.bel.tr",
    "Referer": "https://ulasim.sakarya.bel.tr",
    "User-Agent": "Mozilla/5.0"
}
r = requests.get(url, headers=headers, verify=False)
data = r.json()
print("Total lines:", len(data))
if len(data) > 0:
    for item in data[:2]:
        print(item)
