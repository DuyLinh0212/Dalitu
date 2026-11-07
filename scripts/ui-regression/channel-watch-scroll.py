import argparse
import functools
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import threading
from datetime import datetime, timedelta, timezone
from urllib.parse import parse_qs, urlparse
from playwright.sync_api import TimeoutError as PlaywrightTimeoutError, expect, sync_playwright

parser = argparse.ArgumentParser(description='Verify channel and watch infinite scroll with isolated APIs')
parser.add_argument('--root', type=Path, required=True)
parser.add_argument('--artifacts', type=Path, required=True)
parser.add_argument('--browser-path')
args = parser.parse_args()
ROOT = args.root.resolve()
OUT = args.artifacts
OUT.mkdir(parents=True, exist_ok=True)
NOW = datetime.now(timezone.utc)
CURRENT = 'video-0'
USER = {'userId':'viewer','username':'viewer','email':'viewer@example.test','displayName':'Viewer','isAdmin':False,'emailVerified':True}
CHANNEL = {'channelId':'creator','ownerUserId':'owner','name':'Music','handle':'music','description':'Fixture','settings':'{}','avatarUrl':None,'bannerUrl':None,'subscriberCount':2,'videoCount':75,'isOwner':False,'permissions':[],'createdAt':NOW.isoformat()}
VIDEO = {'videoId':CURRENT,'channelId':'creator','channelName':'Music','channelHandle':'music','title':'Video đang xem','description':'','videoUrl':'','thumbnailUrl':None,'duration':39,'visibility':'public','status':'published','moderationStatus':'approved','languageCode':'vi','createdAt':NOW.isoformat(),'publishedAt':NOW.isoformat(),'ageRestricted':False,'categoryId':'music','tags':['music'],'chapters':[],'stats':{'views':29,'likes':20,'dislikes':0,'comments':0,'averageRating':None,'ratingCount':0,'shares':0},'viewerState':{'rating':None,'reaction':None}}
CARDS = [{'videoId':f'video-{i}','channelId':'creator','channelName':'Music','channelHandle':'music','title':f'Video {i:02}','thumbnailUrl':None,'duration':39,'visibility':'public','publishedAt':NOW.isoformat(),'views':i} for i in range(75)]

class Handler(SimpleHTTPRequestHandler):
    def do_GET(self):
        if not Path(self.translate_path(self.path)).is_file() and '.' not in urlparse(self.path).path.split('/')[-1]: self.path = '/index.html'
        super().do_GET()
    def log_message(self, *_): pass

server = ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(Handler, directory=str(ROOT)))
BASE = f'http://127.0.0.1:{server.server_port}'
HEADERS = {'Access-Control-Allow-Origin': BASE, 'Access-Control-Allow-Credentials': 'true', 'Access-Control-Allow-Headers': '*', 'Access-Control-Allow-Methods': 'GET,POST,PUT,PATCH,DELETE,OPTIONS'}
threading.Thread(target=server.serve_forever, daemon=True).start()
try:
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True, **({'executable_path':args.browser_path} if args.browser_path else {}))
        results = []
        for width in [1440, 440]:
            context = browser.new_context(viewport={'width':width,'height':950}, locale='vi-VN', reduced_motion='reduce')
            context.add_init_script("localStorage.setItem('hutube_lang','vi'); localStorage.setItem('hutube_theme','light');")
            state = {'visibility':'public', 'requests':[], 'fail_page':False}
            def api(route):
                request = route.request; url = urlparse(request.url)
                if '/api/v1/' not in url.path:
                    if request.url.startswith(BASE) or request.url.startswith('blob:'): route.continue_()
                    else: route.abort()
                    return
                if request.method == 'OPTIONS': route.fulfill(status=204, headers=HEADERS); return
                path = url.path.split('/api/v1', 1)[1]; query = parse_qs(url.query)
                number = int(query.get('page', ['1'])[0]); size = int(query.get('pageSize', ['20'])[0])
                data, status = {'items':[],'total':0,'page':1,'pageSize':20}, 200
                if path == '/auth/refresh': data = {'accessToken':'isolated','expiresAt':(NOW+timedelta(hours=1)).isoformat(),'user':USER}
                elif path == '/auth/me': data = USER
                elif path == '/auth/session/player-preferences': data = {'volume':1,'muted':False}
                elif path == '/plans/my-plan': data = {'features':{},'subscription':None}
                elif path == '/videos/' + CURRENT: data = {**VIDEO, 'visibility':state['visibility']}
                elif path == '/videos/' + CURRENT + '/playback': data = {'videoId':CURRENT,'title':VIDEO['title'],'visibility':state['visibility'],'duration':39,'resumeAtSeconds':0,'progress':0,'renditions':[]}
                elif path == '/videos/search':
                    state['requests'].append({'path':path, 'query':query})
                    if state['fail_page'] and number == 2 and 'channelId' in query:
                        state['fail_page'] = False; data, status = {'message':'offline'}, 503
                    else:
                        source = CARDS[:45] if 'tag' in query else CARDS
                        if query.get('sort', [''])[0] == 'oldest': source = list(reversed(source))
                        data = {'items':source[(number-1)*size:number*size], 'total':len(source),'page':number,'pageSize':size}
                elif path in ('/feed/recommended','/feed/home','/feed/resolve'):
                    if request.method == 'POST':
                        payload = request.post_data_json or {}; number=payload.get('page',1); size=payload.get('pageSize',20)
                    state['requests'].append({'path':path, 'query':{'page':[str(number)]}})
                    data = {'items':CARDS[(number-1)*size:number*size], 'total':len(CARDS),'page':number,'pageSize':size}
                elif path == '/channels/me': data, status = {}, 404
                elif path.endswith('/subscribe-status'): data = {'status':'inactive','notificationsEnabled':False}
                elif path.startswith('/channels/') and path not in ('/channels/accessible','/channels/invitations'): data = CHANNEL
                elif path in ('/categories','/violation-types','/subscriptions','/channels/accessible','/channels/invitations') or '/playlists' in path: data = []
                route.fulfill(status=status,headers=HEADERS,content_type='application/json',body=json.dumps(data))
            context.route('**/*', api)
            page = context.new_page(); errors=[]
            page.on('pageerror', lambda error: errors.append(str(error)))
            page.goto(BASE + '/channel/@music?tab=videos&sort=newest')
            cards = page.locator('.channel-video-grid .channel-video-card')
            page.wait_for_function('() => document.querySelectorAll(".channel-video-grid .channel-video-card").length >= 20', timeout=15000)
            for target in [40, 60, 75]:
                page.evaluate('window.scrollTo(0, document.documentElement.scrollHeight)')
                page.wait_for_function('(target) => document.querySelectorAll(".channel-video-grid .channel-video-card").length >= target', arg=target, timeout=10000)
            expect(cards).to_have_count(75)
            expect(page.locator('.channel-video-sentinel')).to_have_count(0)
            page.locator('.channel-video-sort button').nth(1).click()
            expect(page.locator('.channel-video-grid .channel-video-title').first).to_contain_text('Video 74')
            assert any(r['query'].get('sort')==['oldest'] and r['query'].get('page')==['1'] for r in state['requests'] if r['path']=='/videos/search')
            # Returning to newest can serve later pages synchronously from the read cache.
            page.locator('.channel-video-sort button').nth(0).click()
            expect(page.locator('.channel-video-grid .channel-video-title').first).to_contain_text('Video 00')
            for target in [40, 60, 75]:
                page.evaluate('window.scrollTo(0, document.documentElement.scrollHeight)')
                page.wait_for_function('(target) => document.querySelectorAll(".channel-video-grid .channel-video-card").length >= target', arg=target, timeout=10000)
            expect(cards).to_have_count(75)

            page.goto(BASE + '/watch/' + CURRENT)
            expect(page.locator('.video-header h1')).to_have_text('Video đang xem')
            expect(page.locator('.video-header .status-chip')).to_have_count(0)
            tabs = page.locator('.recommendation-filters button')
            cards = page.locator('.recommendation-card')
            page.wait_for_function('() => document.querySelectorAll(".recommendation-card").length >= 19', timeout=10000)

            def wait_for_recommendation_count(target):
                for attempt in range(4):
                    page.evaluate("""() => {
                        const sentinel = document.querySelector('.recommendation-sentinel');
                        if (sentinel) sentinel.scrollIntoView({block: 'end'});
                        else window.scrollTo(0, document.documentElement.scrollHeight);
                    }""")
                    try:
                        page.wait_for_function(
                            '(target) => document.querySelectorAll(".recommendation-card").length >= target',
                            arg=target,
                            timeout=5000,
                        )
                        return
                    except PlaywrightTimeoutError:
                        if page.locator('.recommendation-card').count() >= target:
                            return
                        if attempt == 3:
                            raise

            wait_for_recommendation_count(39)
            tabs.nth(1).click()
            page.wait_for_function('() => document.querySelectorAll(".recommendation-card").length >= 19', timeout=10000)
            wait_for_recommendation_count(39)
            wait_for_recommendation_count(44)
            # Once tagged videos are exhausted, continue through the category without duplicates.
            for target in [59, 74]:
                wait_for_recommendation_count(target)
            expect(page.locator('.recommendation-sentinel')).to_have_count(0)
            links = cards.evaluate_all('(items) => items.map(item => item.getAttribute("href"))')
            assert len(links) == len(set(links)) == 74 and '/watch/'+CURRENT not in links

            page.evaluate('window.scrollTo(0, 0)')
            state['fail_page'] = True
            tabs.nth(2).click(); expect(cards).to_have_count(19)
            page.locator('.recommendation-sentinel').scroll_into_view_if_needed()
            expect(page.locator('.recommendations [role=alert]')).to_be_visible(timeout=10000)
            expect(cards).to_have_count(19)
            page.locator('.recommendations [role=alert] button').click()
            expect(cards).to_have_count(39, timeout=10000)
            page.screenshot(path=str(OUT / f'watch-{width}.png'))

            for visibility, label in [('private','Riêng tư'),('unlisted','Không công khai')]:
                state['visibility'] = visibility
                page.goto(BASE+'/watch/'+CURRENT)
                expect(page.locator('.video-header .status-chip')).to_contain_text(label)
            assert not errors, errors
            results.append({'width':width,'channel_count':75,'watch_count':74,'sort_oldest':True,'retry':True,'visibility':True,'page_errors':errors})
            context.close()
        # A visible continuation button also works without IntersectionObserver.
        state['visibility'] = 'public'
        state['fail_page'] = True
        context = browser.new_context(viewport={'width':440,'height':950}, locale='vi-VN')
        context.add_init_script("localStorage.setItem('hutube_lang','vi'); window.IntersectionObserver = undefined;")
        context.route('**/*', api)
        page = context.new_page()
        page.goto(BASE + '/channel/@music?tab=videos&sort=newest')
        channel_cards = page.locator('.channel-video-grid .channel-video-card')
        expect(channel_cards).to_have_count(20)
        page.locator('.channel-video-sentinel button').click()
        expect(page.locator('.channel-video-sentinel [role=alert]')).to_be_visible()
        expect(channel_cards).to_have_count(20)
        page.locator('.channel-video-sentinel button').click()
        expect(channel_cards).to_have_count(40)
        page.goto(BASE + '/watch/' + CURRENT)
        watch_cards = page.locator('.recommendation-card')
        expect(watch_cards).to_have_count(19)
        page.locator('.recommendation-sentinel button').click()
        expect(watch_cards).to_have_count(39)
        results.append({'width':440,'no_intersection_observer':True,'channel_retry':True})
        context.close()
        browser.close()
        (OUT/'browser-results.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
        print(json.dumps(results))
finally:
    server.shutdown(); server.server_close()
