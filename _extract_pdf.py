import subprocess
import sys

result = subprocess.run(
    [sys.executable, "-m", "pip", "install", "pdfminer.six", "--quiet"],
    capture_output=True, text=True
)
print("pip install result:", result.returncode)
if result.stderr:
    print("pip stderr:", result.stderr[:500])

from pdfminer.high_level import extract_text
text = extract_text(r"D:\Source\ride-lite\RideLite-KienTrucPhanMem.pdf")
with open(r"D:\Source\ride-lite\pdf_clean.txt", "w", encoding="utf-8") as f:
    f.write(text)
print(f"Extracted {len(text)} chars")
print("=== FULL TEXT START ===")
print(text)
print("=== FULL TEXT END ===")
