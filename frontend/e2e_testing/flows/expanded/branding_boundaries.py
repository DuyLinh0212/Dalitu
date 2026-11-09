"""Valid PNGs at the documented exact byte limit, including multipart overhead."""
import struct
import zlib
from playwright.sync_api import expect
from support.avatar_crop import confirm_avatar_crop
from flows.expanded.channels import png_bytes
from flows.expanded.common import case,snapshot_module
from tests.full_functional import register
from support.local_db import LocalDb


def exact_png(size):
    small=png_bytes()
    data=b'0'*(size-len(small)-12)
    kind=b'qcQc'  # Ancillary/private/safe-to-copy PNG chunk before IEND.
    chunk=struct.pack('>I',len(data))+kind+data+struct.pack('>I',zlib.crc32(kind+data)&0xffffffff)
    result=small[:-12]+chunk+small[-12:]
    assert len(result)==size
    return result


def run(suite,config,actors):
    snapshot_module(suite,__file__)
    owner=actors['owner'];p=owner['page']
    suite.go(owner,'/account','app-account-page','/account/profile')
    channel=suite.api(owner,'get','/channels/me').json();cid=channel['channelId']
    for index,(kind,maximum) in enumerate([('avatar',5),('banner',10),('watermark',2)]):
        def exact(entry,index=index,kind=kind,maximum=maximum):
            before=suite.api(owner,'get','/channels/me').json();uploaded=False
            entry['resource_ids']=[cid];entry['input_bytes']=maximum*1024*1024;entry['maximum_file_bytes']=maximum*1024*1024
            try:
                suite.go(owner,'/channel/'+channel['handle']+'/customize','app-channel-settings-page')
                p.locator('.settings-tabs-nav .tab-btn').nth(1).click()
                size=maximum*1024*1024
                payload={'name':'qc-exact-limit.png','mimeType':'image/png','buffer':exact_png(size)}
                if kind=='avatar':
                    suite.api(owner,'post','/channels/'+cid+'/avatar',multipart={'file':payload},expected=200)
                    uploaded=True
                def choose_image():
                    p.locator('.branding-tab-pane input[type=file]').nth(index).set_input_files(payload)
                    if kind=='avatar': confirm_avatar_crop(p)
                response=suite.response(owner,'/channels/'+cid+'/'+kind,
                    choose_image,method='POST',status=200)
                uploaded=True;url=response.json()[kind+'Url'];assert url
                actual=suite.api(owner,'get','/channels/me').json();assert actual[kind+'Url']==url
                p.reload();p.locator('.settings-tabs-nav .tab-btn').nth(1).click()
                image=p.locator('.branding-card').nth(index).locator('img')
                image.evaluate('(i)=>i.decode()');assert image.evaluate('(i)=>i.naturalWidth>0')
                entry['resource_ids']=[cid];entry['input_bytes']=size;entry['maximum_file_bytes']=size
                entry['steps']=['Valid PNG with a CRC-correct private ancillary chunk, exactly '+str(maximum)+'MiB',
                    'Avatar: verify original exact-limit upload through API, then accept that source in browser crop and upload the cropped PNG; other branding: original browser multipart returns 200',
                    'Read persisted exact URL; reload and decode the image','Restore prior branding URL after evidence assertions']
            finally:
                if uploaded:
                    suite.api(owner,'patch','/channels/'+cid,data={kind+'Url':before[kind+'Url']})
                    assert suite.api(owner,'get','/channels/me').json()[kind+'Url']==before[kind+'Url']
        case(suite,owner,'CHANNEL-'+kind.upper()+'-EXACT-MAX-SIZE','U-CHANNEL.02','Valid '+kind+' at inclusive file-size maximum',exact,'channel')


def run_account(suite,config,actors):
    snapshot_module(suite,__file__)
    key='avatar_limit'
    suite.credentials[key]={'email':'qc.avatar.limit.'+suite.run_id.lower()+'@example.test',
        'username':'qc_al_'+suite.run_id.lower().replace('.','_'),'password':'QCAvatarLimit2026!'}
    register(suite,config,key,LocalDb(config))
    actor=suite.actor('account-avatar-limit','user',key);p=actor['page']
    def exact(entry):
        size=5*1024*1024
        entry['resource_ids']=[actor['identity']['userId']];entry['input_bytes']=size
        suite.go(actor,'/account','app-account-page','/account/profile')
        suite.response(actor,'/account/avatar',lambda:p.locator('.channel-avatar-box input[type=file]').set_input_files({
            'name':'qc-exact-avatar.png','mimeType':'image/png','buffer':exact_png(size)}),method='POST',status=200).json()
        value=suite.api(actor,'get','/account/profile').json();assert value['avatarUrl']
        p.reload();expect(p.locator('app-account-page')).to_be_visible()
        p.wait_for_function('(url)=>[...document.images].some(i=>i.src===new URL(url,location.origin).href&&i.complete&&i.naturalWidth>0)',arg=value['avatarUrl'])
        assert suite.api(actor,'get','/account/profile').json()['avatarUrl']==value['avatarUrl']
        entry['steps']=['Fresh isolated verified recipient; existing owner avatar is not changed',
            'Valid PNG exactly 5MiB through actual account file input → 200','Read persisted avatar URL','Reload browser decodes that exact image']
    try:case(suite,actor,'ACCOUNT-AVATAR-EXACT-MAX-SIZE','U-ACCOUNT.01','Valid account avatar at inclusive 5MiB maximum',exact,'account')
    finally:suite.release_actor(actor)
