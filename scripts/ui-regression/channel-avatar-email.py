"""Verify channel avatar crop pixels, cancellation, and client-side email guards."""
import argparse
import base64
from datetime import datetime, timedelta, timezone
from email.parser import BytesParser
from email.policy import default
import json
from pathlib import Path
from urllib.parse import urlparse

from playwright.sync_api import expect, sync_playwright


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-url', default='http://127.0.0.1:4317')
    parser.add_argument('--browser-path')
    parser.add_argument('--artifacts', type=Path, required=True)
    args = parser.parse_args()
    args.artifacts.mkdir(parents=True, exist_ok=True)
    user = {'userId':'viewer-one','username':'viewer','email':'viewer@example.test',
            'displayName':'Kênh âm nhạc','emailVerified':True,'isAdmin':False}
    channel = {'channelId':'channel-one','ownerUserId':user['userId'],'name':'Kênh âm nhạc',
               'handle':'music-channel','description':'','contactEmail':None,'avatarUrl':None,'bannerUrl':None,
               'watermarkUrl':None,'settings':'{}','status':'active','subscriberCount':0,'videoCount':0,
               'isOwner':True,'myRole':'owner','permissions':['channel.edit_profile','channel.edit_branding'],
               'createdAt':'2026-10-26T09:00:00Z'}
    expires = (datetime.now(timezone.utc)+timedelta(hours=2)).isoformat()
    has_channel = False
    writes, uploads, errors = [], [], []

    def api(route):
        nonlocal has_channel
        path = urlparse(route.request.url).path.split('/api/v1',1)[-1]
        method = route.request.method
        headers = {'Access-Control-Allow-Origin':args.base_url,'Access-Control-Allow-Credentials':'true',
                   'Access-Control-Allow-Headers':'*','Access-Control-Allow-Methods':'GET,POST,PATCH,DELETE,OPTIONS'}
        if method=='OPTIONS':
            route.fulfill(status=204,headers=headers)
            return
        data, status = {'items':[],'total':0,'page':1,'pageSize':20}, 200
        if path=='/auth/refresh': data={'accessToken':'isolated-ui-fixture','expiresAt':expires,'user':user}
        elif path=='/auth/me': data=user
        elif path=='/account/profile': data={**user,'avatarUrl':None,'bio':''}
        elif path=='/account/settings': data={'language':'vi','theme':'light'}
        elif path=='/channels/me':
            data, status = (channel,200) if has_channel else ({'code':'CHANNEL_NOT_FOUND'},404)
        elif path=='/channels/accessible': data=[channel] if has_channel else []
        elif path.startswith('/channels/handle/'): data=channel
        elif path=='/channels/check-handle': data={'handle':'music-channel','isAvailable':True,'message':''}
        elif path=='/channels' and method=='POST':
            body=route.request.post_data_json
            writes.append({'path':path,'method':method,'body':body})
            channel.update(body)
            has_channel=True
            data,status=channel,201
        elif path=='/channels/channel-one/avatar' and method=='POST':
            content_type=route.request.headers['content-type']
            message=BytesParser(policy=default).parsebytes(
                ('Content-Type: '+content_type+'\r\nMIME-Version: 1.0\r\n\r\n').encode()+route.request.post_data_buffer)
            part=next(message.iter_parts())
            payload=part.get_payload(decode=True)
            assert part.get_content_type()=='image/png'
            assert payload[:8]==b'\x89PNG\r\n\x1a\n'
            uploads.append(payload)
            channel['avatarUrl']='https://fixture.invalid/avatar'
            data=channel
        elif path=='/channels/channel-one' and method=='PATCH':
            body=route.request.post_data_json
            writes.append({'path':path,'method':method,'body':body})
            channel.update(body)
            data=channel
        elif path=='/channels/channel-one': data=channel
        elif any(name in path for name in ['/roles','/invitations','/subscribed','/subscriptions','/policies','/categories']): data=[]
        elif path=='/plans/my-plan': data={'features':{},'subscription':None}
        elif path.endswith('/negotiate'): data={'negotiateVersion':1,'connectionId':'fixture','connectionToken':'fixture','availableTransports':[]}
        route.fulfill(status=status,headers=headers,content_type='application/json',body=json.dumps(data))

    def dispatch(route):
        if route.request.url=='https://fixture.invalid/avatar':
            route.fulfill(content_type='image/png',body=uploads[-1])
        elif '/api/v1/' in route.request.url: api(route)
        elif route.request.url.startswith(args.base_url): route.continue_()
        else: route.abort()

    with sync_playwright() as p:
        browser=p.chromium.launch(headless=True,**({'executable_path':args.browser_path} if args.browser_path else {}))
        context=browser.new_context(viewport={'width':1440,'height':1000},locale='vi-VN',reduced_motion='reduce')
        context.route('**/*',dispatch)
        context.add_init_script("localStorage.setItem('hutube_lang','vi'); localStorage.setItem('hutube_theme','light');")
        page=context.new_page()
        page.on('pageerror',lambda error:errors.append(str(error)))

        def load(path):
            page.goto(args.base_url+path)
            page.wait_for_load_state('networkidle')

        def fixture(portrait=False):
            encoded=page.evaluate('''(portrait)=>{
                const c=document.createElement('canvas');c.width=portrait?300:600;c.height=portrait?600:300;
                const x=c.getContext('2d');['red','lime','blue'].forEach((color,i)=>{
                    x.fillStyle=color;x.fillRect(portrait?0:i*200,portrait?i*200:0,portrait?300:200,portrait?200:300);
                });return c.toDataURL('image/png').split(',')[1];
            }''',portrait)
            return {'name':'crop-fixture.png','mimeType':'image/png','buffer':base64.b64decode(encoded)}

        def pixel_check(url):
            result=page.evaluate('''async(url)=>{
                const i=new Image();i.src=url;await i.decode();
                const c=document.createElement('canvas');c.width=i.naturalWidth;c.height=i.naturalHeight;
                const x=c.getContext('2d');x.drawImage(i,0,0);
                return {width:c.width,height:c.height,corner:[...x.getImageData(0,0,1,1).data],center:[...x.getImageData(256,256,1,1).data]};
            }''',url)
            assert result=={'width':512,'height':512,'corner':[0,0,0,0],'center':[0,0,255,255]},result
            return result

        def select_blue(portrait=False):
            expect(page.locator('.crop-stage img')).to_be_visible()
            page.locator('#avatar-crop-zoom').evaluate("el=>{el.value='1.5';el.dispatchEvent(new Event('input',{bubbles:true}));}")
            stage=page.locator('.crop-stage')
            stage.focus()
            for _ in range(25):stage.press('ArrowDown' if portrait else 'ArrowRight')

        load('/channel/create')
        expect(page.locator('#channelName')).to_be_visible()
        email=page.locator('#channelEmail')
        for invalid in ['facebook.com/OrinnMusic','a@@example.com','artist@','a..b@example.com']:
            email.fill(invalid)
            expect(email).to_have_attribute('aria-invalid','true')
            expect(page.locator('#channel-email-error')).to_be_visible()
            expect(page.locator('.create-form button[type=submit]')).to_be_disabled()
            email.press('Enter')
            assert not writes and not uploads
        email.fill('music+contact@example.com')
        expect(page.locator('#channel-email-error')).to_have_count(0)
        expect(page.locator('.create-form button[type=submit]')).to_be_enabled()
        avatar=page.locator('.avatar-overlap-box input[type=file]')
        source=fixture()
        avatar.set_input_files(source)
        expect(page.locator('.avatar-crop-dialog')).to_be_visible()
        page.keyboard.press('Escape')
        expect(page.locator('.avatar-crop-dialog')).to_have_count(0)
        expect(page.locator('.avatar-img')).to_have_count(0)
        assert not uploads
        avatar.set_input_files(source)
        select_blue()
        page.screenshot(path=str(args.artifacts/'avatar-crop-desktop.png'))
        page.locator('.apply-button').click()
        expect(page.locator('.avatar-crop-dialog')).to_have_count(0)
        expect(page.locator('.avatar-img')).to_be_visible()
        preview=page.locator('.avatar-img').get_attribute('src')
        pixels=[pixel_check(preview)]
        avatar.set_input_files(source)
        page.locator('.cancel-button').click()
        expect(page.locator('.avatar-img')).to_have_attribute('src',preview)
        assert not uploads
        page.locator('.create-form button[type=submit]').click()
        expect(page.locator('app-channel-create-page')).to_have_count(0)
        assert len(writes)==1 and len(uploads)==1,(writes,len(uploads))
        assert writes[0]['body']['contactEmail']=='music+contact@example.com'
        pixels.append(pixel_check('data:image/png;base64,'+base64.b64encode(uploads[0]).decode()))

        load('/channel/music-channel/customize')
        page.locator('#chEmail').fill('facebook.com/OrinnMusic')
        expect(page.locator('#channel-email-error')).to_be_visible()
        expect(page.locator('.basic-tab-pane .form-actions button')).to_be_disabled()
        assert len(writes)==1
        page.screenshot(path=str(args.artifacts/'channel-email-error.png'))
        page.locator('#chEmail').fill('')
        page.locator('.basic-tab-pane .form-actions button').click()
        expect(page.locator('app-result-notice[tone=success]')).to_be_visible()
        assert len(writes)==2 and writes[-1]['body']['contactEmail']==''
        page.locator('.settings-tabs-nav .tab-btn').nth(1).click()
        avatar=page.locator('.branding-tab-pane input[type=file]').first
        for payload in [
            {'name':'bad.txt','mimeType':'text/plain','buffer':b'bad'},
            {'name':'empty.png','mimeType':'image/png','buffer':b''},
            {'name':'large.png','mimeType':'image/png','buffer':b'0'*(5*1024*1024+1)},
        ]:
            avatar.set_input_files(payload)
            expect(page.locator('app-result-notice[tone=error]')).to_be_visible()
            expect(page.locator('.avatar-crop-dialog')).to_have_count(0)
            assert len(uploads)==1
        avatar.set_input_files({'name':'corrupt.png','mimeType':'image/png','buffer':b'corrupt'})
        expect(page.locator('.crop-error')).to_be_visible()
        expect(page.locator('.apply-button')).to_be_disabled()
        page.keyboard.press('Escape')
        page.set_viewport_size({'width':390,'height':844})
        avatar.set_input_files(fixture(portrait=True))
        expect(page.locator('app-result-notice[tone=error]')).to_have_count(0)
        select_blue(portrait=True)
        box=page.locator('.crop-stage').bounding_box()
        page.mouse.move(box['x']+box['width']/2,box['y']+box['height']/2)
        page.mouse.down();page.mouse.move(box['x']+box['width']/2,box['y']+10,steps=8);page.mouse.up()
        assert len(uploads)==1
        assert page.locator('.avatar-crop-dialog').evaluate('el=>el.scrollWidth<=el.clientWidth')
        page.screenshot(path=str(args.artifacts/'avatar-crop-mobile.png'))
        page.locator('.apply-button').click()
        expect(page.locator('.avatar-crop-dialog')).to_have_count(0)
        expect(page.locator('app-result-notice[tone=success]')).to_be_visible()
        assert len(uploads)==2
        pixels.append(pixel_check('data:image/png;base64,'+base64.b64encode(uploads[-1]).decode()))
        for width in [320,768]:
            page.set_viewport_size({'width':width,'height':844})
            avatar.set_input_files(source)
            expect(page.locator('.crop-stage')).to_be_visible()
            assert page.locator('.avatar-crop-dialog').evaluate('el=>el.scrollWidth<=el.clientWidth')
            page.keyboard.press('Escape')
        page.evaluate("document.documentElement.setAttribute('data-theme','dark')")
        avatar.set_input_files(source)
        expect(page.locator('.crop-stage')).to_be_visible()
        page.screenshot(path=str(args.artifacts/'avatar-crop-dark.png'))
        page.keyboard.press('Escape')
        assert not errors,errors
        result={'checks':'create/settings email guard, optional email, crop cancel/reselect, zoom, keyboard, drag, mobile fit, invalid files, PNG crop pixels and upload bytes','pixels':pixels,'uploads':len(uploads),'page_errors':errors}
        (args.artifacts/'results.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
        print(json.dumps(result,ensure_ascii=False))
        browser.close()


if __name__=='__main__':main()
