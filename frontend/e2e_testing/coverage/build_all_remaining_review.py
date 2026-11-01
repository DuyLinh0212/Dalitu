"""Incremental evidence index; never infer master completion from child counts."""
import argparse,hashlib,json,re,shutil
from collections import Counter
from datetime import datetime,timezone
from pathlib import Path
import sys

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
from support.evidence import run_date_folder

REPO=Path(__file__).resolve().parents[3]
EXCLUDED=['A-CF.01','A-CF.02','A-RECOMMENDATION.03']
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--product-root',type=Path,required=True);args=parser.parse_args()
    root=args.product_root/'frontend/e2e_testing';destination=REPO/'frontend/e2e_testing';out=REPO/'outputs/20261016-all-remaining';out.mkdir(parents=True,exist_ok=True)
    latest={};runs=[];retired=[];active=[]
    for directory in sorted((root/'artifacts/runs').glob('20261016T*Z')):
        if directory.name<'20261016T021000':continue
        path=directory/'report.json'
        if not path.exists():active.append(directory.name);continue
        report=json.loads(path.read_text(encoding='utf-8'));device=report.get('action_device',{}).get('name','desktop')
        if not report.get('finished_at'):active.append(directory.name);continue
        target=destination/'artifacts/runs'/directory.name
        if not target.exists():shutil.copytree(directory,target)
        image_folder=run_date_folder(directory.name)
        images=root/'screenshots'/image_folder;image_target=destination/'screenshots'/image_folder
        if images.exists() and not image_target.exists():shutil.copytree(images,image_target)
        runs.append({'run_id':directory.name,'device':device,'raw_counts':{s:report.get(s,0) for s in ['passed','failed','blocked']},'report':str(target/'report.json')})
        for execution in report.get('group_executions',[]):
            if execution['status']=='completed':
                old=latest.pop((device,'FULL-GROUP-'+execution['group'].upper()),None)
                if old:retired.append({'record':old,'resolved_by_group_execution':directory.name,'note':'No synthetic PASS added'})
        for entry in report['cases']:
            if 'duration_ms' not in entry:continue
            latest[(device,entry.get('logical_id',entry['id']))]={**entry,'action_device':device,'raw_report':str(target/'report.json')}
    invalid_setup=[]
    for key,record in list(latest.items()):
        if record.get('logical_id',record['id'])=='FULL-UPLOAD-INFLIGHT-OFFLINE-RETRY' and record['run_id']=='20261016T022426.264137Z':
            invalid_setup.append({'record':record,'classification':'INVALID_TEST_SETUP',
                'reason':'API returned upload 201 before CDP offline was activated; this attempt never interrupted an in-flight request.',
                'replacement_logical_id':'FULL-UPLOAD-INFLIGHT-CONNECTION-DROP-RETRY',
                'raw_report_preserved':True})
            del latest[key]
        elif record.get('logical_id',record['id'])=='FULL-ACCOUNT-PROFILE-GET-NETWORK-RELOAD' and record['run_id']=='20261016T094641.041967Z':
            invalid_setup.append({'record':record,'classification':'INVALID_TEST_ASSERTION',
                'reason':'Script assumed exactly one profile GET, although multiple app components may read it. Failure occurred at that assertion, before recovery assertions; it is not a verified product defect.',
                'replacement_logical_id':'FULL-ACCOUNT-PROFILE-GET-NETWORK-RELOAD','raw_report_preserved':True})
            del latest[key]
    data={'generated_at':datetime.now(timezone.utc).isoformat(),'release_gate':'NOT_COMPLETE','scope_exclusions':EXCLUDED,
          'source_root':str(args.product_root),'source_revision':'0cfba844 + explicitly recorded QC fixes; does not certify current main',
          'runs':runs,'active_or_unfinished_runs':active,'cases':list(latest.values()),'retired_group_prerequisites':retired,
          'invalid_setup_attempts':invalid_setup,
          'case_counts':dict(Counter(c['status'] for c in latest.values())),
          'limitations':['Child counts include setup and per-device repetitions, not completed master counts.',
             'Claim lease expiry needs a defined product TTL/contract.',
             'Payment tests use QC HMAC injection with fake receiving account, not provider-origin sandbox delivery.',
             'Model training is real Python with Moto storage; R2 TTL integration is separately recorded.',
             'Chromium emulation is not physical hardware/Safari coverage.',
             'Binding/branch reconciliation remains unfinished until every obligation has reviewed mapping.']}
    (out/'review.json').write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
    # Keep requirements as obligations, with candidate evidence only. Parent ID
    # matching is not enough to mark any clause PASS.
    text=(REPO/'docs/WEB_TEST_SCENARIOS.md').read_text(encoding='utf-8');section=text.split('## 5. AUTH',1)[1].split('## 9.',1)[0]
    masters={}
    for line in section.splitlines():
        if not line.startswith('|'):continue
        cells=[x.strip() for x in line.strip('|').split('|')]
        found=next((i for i,c in enumerate(cells[:2]) if re.fullmatch(r'(?:AUTH-[A-Z]+-\d+|[UAX]-[A-Z]+\.\d+)',c)),None)
        if found is None:continue
        id=cells[found];requirement=cells[found+2] if len(cells)>found+2 else ''
        masters[id]={'master':id,'status':'OUT_OF_SCOPE' if id in EXCLUDED else 'NEEDS_CLAUSE_RECONCILIATION',
                     'requirement':requirement,'clauses':[s.strip() for s in requirement.split(';') if s.strip()],
                     'candidate_case_ids':sorted({c.get('logical_id',c['id']) for c in latest.values() if c['parent']==id})}
    (out/'master-obligations.json').write_text(json.dumps(list(masters.values()),ensure_ascii=False,indent=2),encoding='utf-8')
    manifest=[]
    for relative in ['backend/src/HuTube.Infrastructure/Storage/LocalStorageService.cs',
        'backend/src/HuTube.Infrastructure/Persistence/Migrations/20261016093000_AppealEscalationResolutionState.cs',
        'backend/src/HuTube.Infrastructure/Recommendations/RecommendationAdminService.cs',
        'backend/src/HuTube.Infrastructure/Videos/ContentService.cs',
        'backend/src/HuTube.Infrastructure/Videos/VideoRenditionProcessing.cs',
        'backend/src/HuTube.Infrastructure/Payments/PaymentService.cs',
        'backend/tests/HuTube.UnitTests/PaymentServiceTests.cs',
        'backend/tests/HuTube.UnitTests/LocalStorageEvidenceTests.cs',
        'frontend/admin-web/src/app/features/moderation/admin-moderation-page.ts',
        'frontend/admin-web/src/app/features/moderation/admin-moderation-page.html',
        'frontend/admin-web/src/app/features/recommendations/recommendations-page.ts',
        'frontend/admin-web/src/app/core/admin-page-translations.ts',
        'frontend/user-web/src/app/features/explore/explore-page.ts',
        'frontend/user-web/src/app/features/policy/public-policy-page.ts',
        'frontend/user-web/src/app/features/policy/public-policy-page.html',
        'frontend/user-web/src/app/features/policy/public-policy-page.scss',
        'frontend/user-web/src/app/core/i18n.service.ts',
        'frontend/e2e_testing/run_local.py']:
        source=args.product_root/relative
        if source.exists():manifest.append({'path':relative,'sha256':hashlib.sha256(source.read_bytes()).hexdigest()})
    (out/'changed-source-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
    print(json.dumps({'runs':len(runs),'counts':data['case_counts'],'unfinished':active,'master_obligations':len(masters)}))

if __name__=='__main__':main()
