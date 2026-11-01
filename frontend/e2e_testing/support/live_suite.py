"""Functional E2E against user-authorized running loopback services.

No SQL, role elevation, server lifecycle management, API mocks or stored secrets.
Credentials are read from WEB_E2E_CREDENTIALS. Reports never contain access tokens.
"""
import json
import os
import re
import time
import traceback
import hashlib
import shutil
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlsplit,parse_qs

from playwright.sync_api import expect
from support.evidence import screenshot_path

ROOT = Path(__file__).resolve().parents[1]
VIEWPORTS = {'desktop': {'width':1440,'height':960}, 'mobile': {'width':390,'height':844}, 'tablet': {'width':768,'height':1024}}

class PrerequisiteMissing(RuntimeError):
    pass

class LiveSuite:
    def __init__(self, playwright, headed=False, environment=None, run_dir=None):
        self.credentials = json.loads(os.environ['WEB_E2E_CREDENTIALS'])
        self.run_id = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S.%fZ')
        self.run_dir = ROOT / 'artifacts/runs' / self.run_id
        if run_dir is not None:
            managed_dir = Path(run_dir).resolve()
            if managed_dir.parent != (ROOT / 'artifacts/runs').resolve() or not re.fullmatch(r'\d{8}T\d{6}\.\d+Z', managed_dir.name):
                raise ValueError('Managed report directory must be an owned timestamp directory')
            if (managed_dir / 'report.json').exists():
                raise ValueError('Refusing to overwrite an existing test report')
            self.run_dir = managed_dir
            self.run_id = managed_dir.name
        self.run_dir.mkdir(parents=True, exist_ok=run_dir is not None)
        self.report = {'suite':'web-live-functional','run_id':self.run_id,'started_at':datetime.now(timezone.utc).isoformat(), 'timezone':'Asia/Saigon','cases':[], 'http_statuses':[], 'page_errors':[], 'setup':[], 'cleanup':[], 'coverage_claim':'Partial until every master scenario and binding has executable evidence', 'environment':{'user':'http://localhost:4200','admin':'http://localhost:4201'}}
        if environment:
            assert set(environment)=={'user','admin'}
            assert all(urlsplit(v).hostname in ('localhost','127.0.0.1','::1') for v in environment.values())
            self.report['environment']=environment
            self.report['environment_kind']='isolated-local-postgresql-fixtures'
        options = {'headless':not headed}
        self.playwright = playwright
        chrome = os.environ.get('CHROME_BIN')
        if chrome or os.name == 'nt':
            options['executable_path'] = chrome or r'C:\Program Files\Google\Chrome\Application\chrome.exe'
        self.browser = playwright.chromium.launch(**options)
        self.report['browser_version']=self.browser.version
        self.input_device=os.environ.get('WEB_E2E_DEVICE','desktop')
        if self.input_device not in VIEWPORTS:raise ValueError('Unsupported action device')
        self.action_viewport=VIEWPORTS[self.input_device]
        self.report['action_device']={'name':self.input_device,'viewport':self.action_viewport,
            'kind':'Desktop Chromium' if self.input_device=='desktop' else 'Chromium responsive device emulation; no physical-device claim'}
        if self.input_device!='desktop':
            from support.touch_actions import install_touch_actions
            install_touch_actions()
            self.report['action_device']['pointer_input']='Real Chromium tap for locator clicks/check/uncheck in touch-enabled contexts'
        self.report['test_source_snapshot']=[]
        for folder in ('flows','support','tests','coverage'):
            for source in (ROOT/folder).rglob('*.py'):
                if '__pycache__' in source.parts:continue
                relative=source.relative_to(ROOT)
                target=self.run_dir/'test-sources'/relative
                target.parent.mkdir(parents=True,exist_ok=True)
                shutil.copyfile(source,target)
                self.report['test_source_snapshot'].append({'path':relative.as_posix(),'sha256':hashlib.sha256(source.read_bytes()).hexdigest()})
        self.actors = {}
        expect.set_options(timeout=15000)
        self.current = None
        self.last_navigation=0.0
        self.last_auth_api=0.0

    def actor(self, name, application, credential_key=None, page=None):
        credential_key = credential_key or application
        actor = {'name':name,'application':application,'origin':self.report['environment'][application], 'headers':{}}
        shared_context = page is not None
        context = page.context if shared_context else self.browser.new_context(viewport=self.action_viewport,
            is_mobile=self.input_device!='desktop',has_touch=self.input_device!='desktop',
            locale='vi-VN',reduced_motion='reduce',permissions=['clipboard-read','clipboard-write'])
        if not shared_context:context._qc_touch_actions=self.input_device!='desktop'
        if not shared_context:
            context.add_init_script('''
              window.__qcUIEvents=[];
              for (const type of ['click','change','input','submit','keydown','pointerdown','pointerup','touchstart']) document.addEventListener(type,event=>{
                const target=event.target;if (!(target instanceof Element))return;
                let component=target;while(component && !component.tagName.toLowerCase().startsWith('app-'))component=component.parentElement;
                window.__qcUIEvents.push({at:Date.now(),type,pointerType:event.pointerType||(type==='touchstart'?'touch':''),tag:target.tagName.toLowerCase(),id:target.id||'',name:target.getAttribute('name')||'',classes:target.getAttribute('class')||'',component:component?.tagName.toLowerCase()||'',route:location.pathname});
                if(window.__qcUIEvents.length>2000)window.__qcUIEvents.shift();
              },true);
            ''')
        actor['ui_event_log']=[]
        page = page if shared_context else context.new_page()
        actor['shared_context'] = shared_context
        # Every hard navigation (including existing flow reloads) shares one
        # throttle. A go()-only throttle misses reload-heavy account tests.
        for method in ('goto','reload'):
            original=getattr(page,method)
            def paced(*args,_original=original,**kwargs):
                try:
                    actor['ui_event_log'].extend(page.evaluate('()=>window.__qcUIEvents||[]'))
                    page.evaluate('()=>{window.__qcUIEvents=[]}')
                except Exception:pass
                if getattr(page,'_qc_navigation_ready',False):
                    page._qc_navigation_ready=False
                else:
                    self.prepare_navigation(page)
                    page._qc_navigation_ready=False
                return _original(*args,**kwargs)
            paced._qc_navigation=True
            setattr(page,method,paced)
        page.set_default_timeout(15000)
        page.set_default_navigation_timeout(45000)
        actor.update(context=context,page=page)
        page.on('close',lambda:self.report.setdefault('page_closures',[]).append({
            'actor':name,'case':self.current,'expected_cleanup':bool(actor.get('closing')),
            'at':datetime.now(timezone.utc).isoformat()}))
        page.on('crash',lambda:self.report.setdefault('page_crashes',[]).append({
            'actor':name,'case':self.current,'at':datetime.now(timezone.utc).isoformat()}))
        self.actors[name] = actor
        def request_observer(request):
            if urlsplit(request.url).path.startswith('/api/v1/') and request.headers.get('authorization'):
                actor['headers']['Authorization'] = request.headers['authorization']
        page.on('request',request_observer)
        page.on('response',lambda r: self.report['http_statuses'].append({'actor':name,'case':self.current,'method':r.request.method,'path':urlsplit(r.url).path,'status':r.status}))
        page.on('pageerror',lambda e: self.report['page_errors'].append({'actor':name,'case':self.current,'type':e.name,'message':re.sub(r'https?://\S+','[url]',e.message)[:500]}))
        if credential_key == 'guest':
            return actor
        page.goto(actor['origin']+'/login')
        page.locator('#email').fill(self.credentials[credential_key]['email'])
        page.locator('#password').fill(self.credentials[credential_key]['password'])
        page.locator('#password').press('Tab')
        expect(page.locator('#email')).to_have_class(re.compile(r'\bng-valid\b'))
        expect(page.locator('#password')).to_have_class(re.compile(r'\bng-valid\b'))
        expect(page.locator('button[type=submit]')).to_be_enabled()
        with page.expect_response(lambda r:r.request.method=='POST' and urlsplit(r.url).path=='/api/v1/auth/login',timeout=30000) as pending:
            page.locator('button[type=submit]').click()
        response = pending.value
        assert response.status == 200, 'Login prerequisite HTTP '+str(response.status)
        actor['api_url'] = urlsplit(response.url)._replace(path='/api/v1',query='',fragment='').geturl()
        assert urlsplit(actor['api_url']).hostname in ('localhost','127.0.0.1','::1')
        actor['identity'] = response.json()['user']
        assert actor['identity']['email'].lower() == self.credentials[credential_key]['email'].lower()
        page.wait_for_url('**/'+('home' if application=='user' else 'account'))
        expect(page.locator('app-home-page' if application=='user' else 'app-account-page')).to_be_visible(timeout=45000)
        self.report['setup'].append({'actor':name,'application':application,'status':'passed','user_id':actor['identity']['userId'],'is_admin':actor['identity'].get('isAdmin')})
        return actor

    def prepare_navigation(self,page):
        delay=6.0-(time.monotonic()-self.last_navigation)
        if delay>0: page.wait_for_timeout(delay*1000)
        self.last_navigation=time.monotonic()
        page._qc_navigation_ready=True

    def go(self, actor, route, selector, endpoint=None, params=None):
        page = actor['page']
        # These journeys switch applications sequentially. Park the other main
        # authenticated document to stop its native 3-second polling, keeping
        # cookies/session intact. Dedicated session actors remain simultaneous
        # so revocation assertions still exercise live devices.
        if actor['name'] in ('owner','admin','second-supplied-account'):
            for name,other in self.actors.items():
                if name in ('owner','second-supplied-account') and other is not actor and not other.get('released') and other['page'].url!='about:blank':
                    other['page'].goto('about:blank')
        page.set_viewport_size(self.action_viewport)
        # Wait before arming the response listener. Otherwise a response from
        # the previous document can win during throttle delay and lose its body
        # when goto destroys that document.
        self.prepare_navigation(page)
        if endpoint:
            # A polling GET from the outgoing document may finish while goto's
            # wrapper collects UI events. Only accept requests issued after the
            # new main document has committed, never an outgoing poll body.
            navigation = {'committed':False, 'requests':set()}
            def committed(frame):
                if frame == page.main_frame:
                    navigation['committed']=True
            def requested(request):
                if navigation['committed']:
                    navigation['requests'].add(request)
            page.on('framenavigated',committed)
            page.on('request',requested)
            try:
                with page.expect_response(lambda r:r.request in navigation['requests'] and r.request.method=='GET' and urlsplit(r.url).path == '/api/v1'+endpoint and all(parse_qs(urlsplit(r.url).query).get(key,[''])[0]==str(value) for key,value in (params or {}).items()),timeout=30000) as pending:
                    page.goto(actor['origin']+route)
                response = pending.value
                assert response.status == 200, 'Load HTTP '+str(response.status)
                data = response.json()
            finally:
                page.remove_listener('framenavigated',committed)
                page.remove_listener('request',requested)
        else:
            page.goto(actor['origin']+route)
            data = None
        expect(page.locator(selector)).to_be_visible(timeout=45000)
        return data

    def api(self, actor, method, path, expected=200, **kwargs):
        # Reuse only the claim actually acquired by THIS actor/context. Never
        # fetch a newer token here: doing so would conceal stale-tab failures.
        if method.lower() == 'post' and path.rsplit('/',1)[-1] in ('release','resolve','dispositions'):
            lease = actor.get('_claims',{}).get(path.rsplit('/',1)[0])
            if lease and isinstance(kwargs.get('data',{}),dict) and 'claimId' not in kwargs.get('data',{}):
                kwargs['data']={**kwargs.get('data',{}),'claimId':lease}
        if path.startswith('/auth/'):
            delay=6.0-(time.monotonic()-self.last_auth_api)
            if delay>0: actor['page'].wait_for_timeout(delay*1000)
            self.last_auth_api=time.monotonic()
        try:
            response = getattr(actor['context'].request,method)(actor['api_url']+path,headers=actor['headers'],timeout=30000,**kwargs)
        except Exception as error:
            # Playwright fetch exceptions include Authorization/Cookie call logs.
            # Retain connection diagnostics, never those headers.
            diagnostic=str(error).split('Call log:')[0][:400]
            diagnostic=re.sub(r'eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+','[redacted-jwt]',diagnostic)
            raise RuntimeError('Network request failed '+method.upper()+' '+path.split('?')[0]+': '+diagnostic) from None
        self.report['http_statuses'].append({'actor':actor['name'],'case':self.current,'method':method.upper(),'path':urlsplit(actor['api_url']+path).path,'status':response.status})
        assert response.status in (expected if isinstance(expected,tuple) else (expected,)), 'Read-back HTTP '+str(response.status)+' '+path.split('?')[0]
        self.remember_claim(actor,path,response)
        return response

    def remember_claim(self,actor,path,response):
        if path.endswith('/claim') and 200 <= response.status < 300:
            value=response.json()
            if value.get('claimId'):actor.setdefault('_claims',{})[path.rsplit('/',1)[0]]=value['claimId']

    def response(self, actor, path, action, method='GET', status=200, params=None):
        if getattr(action,'_qc_navigation',False):
            self.prepare_navigation(actor['page'])
        def matches(r):
            query=parse_qs(urlsplit(r.url).query)
            return r.request.method == method and urlsplit(r.url).path == '/api/v1'+path and all(query.get(key,[''])[0]==str(value) for key,value in (params or {}).items())
        with actor['page'].expect_response(matches,timeout=30000) as pending:
            action()
        response = pending.value
        assert response.status in (status if isinstance(status,tuple) else (status,)), 'UI request HTTP '+str(response.status)
        self.remember_claim(actor,path,response)
        return response

    def evidence(self, actor, case, step, responsive_assert=None, full_page=True):
        page = actor['page']
        if page.url=='about:blank':
            # A parked document has Chromium's default mobile layout width 980.
            # It cannot provide application responsive evidence for an API case.
            page.goto(actor['origin']+'/login')
            page.wait_for_function('()=>!!document.querySelector("app-auth-page,app-home-page,app-account-page")')
            case['evidence_context']='Previously parked API actor navigated through the actual login/session guard for application viewport context; this is not evidence of an Admin-denial page layout'
        failures = []
        for viewport, dimensions in VIEWPORTS.items():
            page.set_viewport_size(dimensions)
            if responsive_assert:
                responsive_assert()
            # Measure document and visible dialog bounds after layout has settled.
            metrics = page.evaluate('''() => ({width:innerWidth,scrollWidth:document.documentElement.scrollWidth, dialogs:[...document.querySelectorAll('.modal-dialog,[role=dialog]')].filter(e=>e.getClientRects().length).map(e=>{const r=e.getBoundingClientRect();return {left:r.left,right:r.right,top:r.top,bottom:r.bottom};})})''')
            if metrics['scrollWidth'] > dimensions['width']+1:
                failures.append(viewport+': horizontal overflow '+str(metrics['scrollWidth']))
            for dialog in metrics['dialogs']:
                if dialog['left'] < -1 or dialog['right'] > dimensions['width']+1:
                    failures.append(viewport+': dialog clipped horizontally')
            path = ROOT/screenshot_path(self.run_id, self.report['suite'], actor['application'],
                                        case['id'], step, case['module'], viewport)
            path.parent.mkdir(parents=True,exist_ok=True)
            page.screenshot(path=str(path),full_page=full_page,animations='disabled',mask=[page.locator('input[type=password]')],timeout=45000)
            case['screenshots'].append({'viewport':viewport,'step':step,'path':path.relative_to(ROOT).as_posix(),'metrics':metrics})
        page.set_viewport_size(self.action_viewport)
        case.setdefault('responsive_failures',[]).extend(failures)

    def case(self, actor, case_id, parent, name, expected, action, module=None):
        logical_id=case_id
        # Delta runs intentionally preserve approved evidence.  Check before
        # adding an entry or executing a UI/API operation so a typo in a group
        # cannot turn a retained case into an accidental rerun.
        from support.delta_scope import assert_allowed
        assert_allowed(parent, logical_id)
        prior_attempts=sum(c.get('logical_id',c['id'])==logical_id for c in self.report['cases'])
        if prior_attempts:case_id=logical_id+'--attempt-'+str(prior_attempts+1)
        previous_case = self.current
        self.current = case_id
        entry = {'id':case_id,'parent':parent,'name':name,'expected':expected,'application':actor['application'],'actor':actor['name'],'module':module or parent.split('-')[-1].split('.')[0].lower(),'status':'failed','screenshots':[],'steps':[],'assertions':[],'resource_ids':[],'run_id':self.run_id,'tester':'Codex QC automation','precondition':'Existing supplied identity; loopback services running; fixture IDs are obtained from actual responses','storage_assertion':'N/A unless explicitly stated; API read-back is used without direct SQL','mail_manual':'NOT_EXECUTED'}
        if self.report.get('environment_kind')=='isolated-local-postgresql-fixtures':
            entry['precondition']='Synthetic independent actors; owned local PostgreSQL/API/UI; IDs from real responses; initial Super Admin bootstrapped only as fixture setup'
        entry['logical_id']=logical_id
        entry['attempt_index']=prior_attempts+1
        self.report['cases'].append(entry)
        start = time.monotonic()
        event_start=time.time()*1000
        errors_before = len(self.report['page_errors'])
        try:
            action(entry)
            entry['functional_status'] = 'passed'
            if not entry['screenshots']:
                self.evidence(actor,entry,'result')
            assert not entry.get('responsive_failures'), '; '.join(entry.get('responsive_failures',[]))
            assert len(self.report['page_errors']) == errors_before, 'Uncaught page error during case'
            entry['status'] = 'passed'
            entry['actual'] = 'All recorded UI/API/persistence assertions completed; three viewport evidence captured.'
        except PrerequisiteMissing as error:
            entry.update(status='blocked',actual=str(error))
        except Exception as error:
            entry.update(error_type=type(error).__name__,actual=str(error)[:1500])
            entry['actual']=re.sub(r'eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+','[redacted-jwt]',entry['actual'])
            entry['actual']=re.sub(r'([?&]token=)[^&\s<>"\']+',r'\1[redacted-token]',entry['actual'])
            entry['actual']=re.sub(r'(?im)^.*(?:authorization|cookie|set-cookie):.*$','[redacted-auth-header]',entry['actual'])
            entry['failure_location']=[{'file':Path(frame.filename).name,'line':frame.lineno,'function':frame.name} for frame in traceback.extract_tb(error.__traceback__)[:5]]
            # Never save login assertion text or credentials.
            for values in self.credentials.values():
                entry['actual'] = entry['actual'].replace(values['password'],'[redacted]')
            try:
                self.evidence(actor,entry,'failure')
            except Exception:
                entry['screenshot_error'] = True
        entry['route'] = urlsplit(actor['page'].url).path
        entry['query_state']={k:v for k,v in parse_qs(urlsplit(actor['page'].url).query).items() if k in ('q','sort','duration','dateRange','page','tab','channelId')}
        entry['duration_ms'] = round((time.monotonic()-start)*1000)
        entry['ui_events']=[]
        for observed in self.actors.values():
            events=list(observed.get('ui_event_log',[]))
            if not observed.get('released'):
                try:events+=observed['page'].evaluate('()=>window.__qcUIEvents||[]')
                except Exception:pass
            entry['ui_events'].extend({'actor':observed['name'],**event} for event in events if event['at']>=event_start)
        print(case_id,entry['status'],entry.get('actual','')[:180],flush=True)
        self.flush()
        self.current = previous_case
        return entry['status'] == 'passed'

    def flush(self):
        for status in ('passed','failed','blocked','planned'):
            self.report[status] = sum(c['status']==status for c in self.report['cases'])
        (self.run_dir/'report.json').write_text(json.dumps(self.report,ensure_ascii=False,indent=2),encoding='utf-8')
        (self.run_dir/'http-statuses.json').write_text(json.dumps(self.report['http_statuses'],ensure_ascii=False,indent=2),encoding='utf-8')

    def release_actor(self,actor):
        if actor.get('released'):
            return
        if actor.get('api_url') and actor.get('identity'):
            try:
                if not actor['page'].is_closed():actor['page'].wait_for_timeout(6000)
                headers={'X-HuTube-Client':'web'}
                if actor['application']=='admin': headers['X-HuTube-App']='admin'
                response=actor['context'].request.post(actor['api_url']+'/auth/logout',headers=headers,data={},timeout=15000)
                if response.status==429:
                    # This browser's native revocation watchdog also consumes
                    # auth permits. Stop its document polling during cooldown,
                    # while retaining its own cookies for a bounded logout retry.
                    actor['page'].goto('about:blank')
                    actor['page'].wait_for_timeout(55000)
                    response=actor['context'].request.post(actor['api_url']+'/auth/logout',headers=headers,data={},timeout=15000)
                self.report['cleanup'].append({'resource':'this test browser login session','actor':actor['name'],'status':'signed-out' if response.status==200 else 'cleanup-failed','http_status':response.status})
            except Exception as error:
                self.report['cleanup'].append({'resource':'this test browser login session','actor':actor['name'],'status':'cleanup-failed','error_type':type(error).__name__})
        actor['closing']=True
        if actor.get('shared_context'): actor['page'].close()
        else: actor['context'].close()
        actor['released']=True

    def close(self):
        for actor in self.actors.values():
            self.release_actor(actor)
        self.report['finished_at'] = datetime.now(timezone.utc).isoformat()
        self.flush()
        (ROOT/'artifacts/live-latest.json').write_text(json.dumps({'run_dir':str(self.run_dir),'run_id':self.run_id},indent=2),encoding='utf-8')
        self.browser.close()
