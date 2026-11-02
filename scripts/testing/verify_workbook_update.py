"""Independent read-only XLSX/traceability checks; no Excel writer dependency."""
from __future__ import annotations

import collections
import json
import re
import sys
import xml.etree.ElementTree as ET
import zipfile
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "docs/testing/runs/2026-10-23-planned"
NS = {"m": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}


def read(path):
    with zipfile.ZipFile(path) as archive:
        assert archive.testzip() is None
        strings = ["".join(t.itertext()) for t in ET.fromstring(archive.read("xl/sharedStrings.xml")).findall("m:si", NS)] if "xl/sharedStrings.xml" in archive.namelist() else []
        relations = {r.attrib["Id"]: r.attrib["Target"] for r in ET.fromstring(archive.read("xl/_rels/workbook.xml.rels"))}
        sheets = {}
        for sheet in ET.fromstring(archive.read("xl/workbook.xml")).find("m:sheets", NS):
            part = relations[sheet.attrib["{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id"]]
            part = part.lstrip("/") if part.startswith("/") else "xl/" + part
            cells = {}
            for cell in ET.fromstring(archive.read(part)).findall(".//m:sheetData/m:row/m:c", NS):
                raw, formula = cell.find("m:v", NS), cell.find("m:f", NS)
                kind = cell.attrib.get("t")
                value = strings[int(raw.text)] if kind == "s" and raw is not None else "".join(cell.find("m:is", NS).itertext()) if kind == "inlineStr" else raw.text if raw is not None else ""
                if value or formula is not None:
                    cells[cell.attrib["r"]] = {"value": value, "formula": formula.text if formula is not None else None, "type": kind}
            sheets[sheet.attrib["name"]] = cells
        return sheets


def main():
    if "--fixes" in sys.argv:
        return verify_live("fixes")
    if "--live" in sys.argv:
        return verify_live()
    original = read(ROOT / ".work/planned-20261023/HuTube_Test.before.xlsx")
    edited = read(ROOT.parent / "Docs/HuTube_Test.xlsx")
    report = json.loads((OUT / "case-results.json").read_text(encoding="utf-8"))
    assert list(original) == list(edited)
    untouched = [s for s in original if s not in {"Summary", "Backend Tests", "Backend Executions"}]
    for name in untouched: assert original[name] == edited[name], f"Unexpected edit: {name}"
    cases = edited["Backend Tests"]
    ids = {cell["value"]: int(address[1:]) for address, cell in cases.items() if re.fullmatch(r"A\d+", address) and int(address[1:]) >= 6}
    assert len(ids) == 530
    old_ids = {cell["value"] for address, cell in original["Backend Tests"].items() if re.fullmatch(r"A\d+", address) and int(address[1:]) >= 6}
    assert set(ids) == old_ids
    assert len(report["cases"]) == 48
    for result in report["cases"]:
        row = ids[result["id"]]
        assert cases[f"I{row}"]["value"] == result["status"]
        date = cases[f"L{row}"]
        assert date["type"] in ("d", "n", None), "Execution date must be a typed date or Excel serial"
        if date["type"] == "d": datetime.fromisoformat(date["value"].replace("Z", "+00:00"))
        else: assert float(date["value"]) > 40000
        assert result["actual"] in cases[f"H{row}"]["value"]
        for evidence in result["evidence"]:
            assert (ROOT/evidence if evidence.startswith("backend/") else OUT/evidence).is_file(), evidence
    changed_ids = {r["id"] for r in report["cases"]}
    for case_id in old_ids-changed_ids:
        row = ids[case_id]
        for column in "GHIJKL":
            assert original["Backend Tests"].get(f"{column}{row}") == cases.get(f"{column}{row}")
    original_execution = original["Backend Executions"]
    for address, value in original_execution.items():
        if int(re.sub(r"\D", "", address)) >= 6:
            assert value == edited["Backend Executions"][address], f"Historical execution changed: {address}"
    new_ids = [cell["value"] for address, cell in edited["Backend Executions"].items()
               if address.startswith("A") and cell["value"].startswith(report["runId"]+"-")]
    assert len(new_ids) == len(set(new_ids)) == len(report["executions"])+48
    assert all(cell["type"] != "e" for sheet in edited.values() for cell in sheet.values())
    counts = collections.Counter(cases[f"I{row}"]["value"] for row in ids.values())
    assert counts["Pass"] == 482+report["counts"].get("Pass",0)
    for status in ["Fail", "Blocked", "N/A"]: assert counts[status] == report["counts"].get(status,0)
    for address, expected in {"F12": counts["Pass"], "G12": counts["Fail"], "H12": counts["Blocked"], "J12": counts["N/A"]}.items():
        assert float(edited["Summary"][address]["value"]) == expected
    result = {"workbook": str(ROOT.parent/"Docs/HuTube_Test.xlsx"), "sheetCount": len(edited),
              "untouchedSheetsVerified": untouched, "catalogIds": len(ids), "historicalCaseResultsPreserved": 482,
              "newExecutionRows": len(new_ids), "caseCounts": dict(counts), "formulaErrors": 0, "result": "PASS"}
    (OUT / "workbook-verification.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False))


def verify_live(mode="live"):
    fixing = mode == "fixes"
    live_out = ROOT / ("docs/testing/runs/2026-10-23-fixes" if fixing else "docs/testing/runs/2026-10-23-blocked")
    original = read(ROOT / f".work/planned-20261023/HuTube_Test.before-{mode}.xlsx")
    edited = read(ROOT.parent / "Docs/HuTube_Test.xlsx")
    report = json.loads((live_out / "case-results.json").read_text(encoding="utf-8"))
    assert list(original) == list(edited)
    untouched = [s for s in original if s not in {"Summary", "Backend Tests", "Backend Executions"}]
    for name in untouched:
        assert original[name] == edited[name], name
    cases = edited["Backend Tests"]
    ids = {cell["value"]: int(address[1:]) for address, cell in cases.items()
        if re.fullmatch(r"A\d+", address) and int(address[1:]) >= 6}
    assert len(ids) == 530 and len(set(ids.values())) == 530
    changed = {c["id"]: c for c in report["cases"]}
    expected_counts = collections.Counter()
    for case_id, row in ids.items():
        assert original["Backend Tests"][f"A{row}"]["value"] == case_id
        if case_id not in changed:
            for col in "ABCDEFGHIJKL":
                assert original["Backend Tests"].get(f"{col}{row}") == cases.get(f"{col}{row}"), f"Unrelated case changed {case_id}/{col}"
            expected_counts[cases[f"I{row}"]["value"]] += 1
            continue
        c = changed[case_id]
        assert original["Backend Tests"][f"I{row}"]["value"] == ("Fail" if fixing else "Blocked")
        assert cases[f"I{row}"]["value"] == c["status"]
        assert c["actual"] in cases[f"H{row}"]["value"]
        expected_counts[c["status"]] += 1
        preserved = "ABCDEK" if case_id == "B-PYAPI.02" and fixing else "ACK" if case_id == "B-MEDIA.04" else "ABCDEFK"
        for col in preserved:
            assert original["Backend Tests"].get(f"{col}{row}") == cases.get(f"{col}{row}")
        if c.get("expected"):
            assert cases[f"F{row}"]["value"] == c["expected"]
        assert cases[f"L{row}"]["type"] in ("d", "n", None)
        for evidence in c["evidence"]:
            assert (live_out / evidence).is_file()
    for address, value in original["Backend Executions"].items():
        if int(re.sub(r"\D", "", address)) >= 6:
            assert edited["Backend Executions"][address] == value, f"Historical execution changed {address}"
    new_ids = [c["value"] for a, c in edited["Backend Executions"].items()
        if re.fullmatch(r"A\d+", a) and c["value"].startswith(report["runId"]+"-")]
    assert len(new_ids) == len(set(new_ids)) == len(report["cases"])+len(report["executions"])
    assert len(report["cases"]) == (5 if fixing else 8)
    assert not any(c["type"] == "e" for sheet in edited.values() for c in sheet.values())
    for address, status in {"F12": "Pass", "G12": "Fail", "H12": "Blocked", "J12": "N/A"}.items():
        assert float(edited["Summary"][address]["value"]) == expected_counts[status]
    last_run_row = max(int(a[1:]) for a,c in original["Summary"].items()
        if re.fullmatch(r"B\d+", a) and 26 <= int(a[1:]) <= 100 and c["value"].isdigit())
    for a, c in original["Summary"].items():
        if re.fullmatch(r"[B-J]\d+", a) and 26 <= int(a[1:]) <= last_run_row:
            assert edited["Summary"][a] == c, f"Previous run history changed {a}"
    result = {"result": "PASS", "catalogIds": len(ids), "changedCases": len(report["cases"]), "newExecutionRows": len(new_ids),
        "caseCounts": dict(expected_counts), "untouchedSheetsVerified": untouched,
        "priorExecutionHistoryPreserved": True, "priorRunSummaryPreserved": True, "formulaErrors": 0}
    (live_out / "workbook-verification.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    main()
