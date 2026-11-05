"""Live channel field and branding rejection, preserving the existing channel."""
from playwright.sync_api import expect

def run(suite,owner):
    page=owner['page']
    baseline=suite.api(owner,'get','/channels/me').json()
    path='/channels/'+baseline['channelId']
    route='/channel/'+baseline['handle']+'/customize'
    def unchanged():
        value=suite.api(owner,'get','/channels/me').json()
        for field in ['name','handle','description','contactEmail','settings','avatarUrl','bannerUrl','watermarkUrl']:
            assert value.get(field)==baseline.get(field), 'Channel field changed: '+field
    for field,value in [('name','   '),('handle','x'),('link','javascript:alert(1)')]:
        def invalid(case,field=field,value=value):
            suite.go(owner,route,'app-channel-settings-page')
            if field=='link':
                page.locator('.btn-add-link').click()
                page.locator('.link-row-card').last.locator('input[type=url]').fill(value)
            else: page.locator('#chName' if field=='name' else '#chHandle').fill(value)
            page.locator('.basic-tab-pane .form-actions button').click()
            expect(page.locator('app-result-notice[tone=error]')).to_be_visible()
            unchanged()
            suite.evidence(owner,case,'validation-error')
            page.reload()
            expect(page.locator('#chName')).to_have_value(baseline['name'])
            expect(page.locator('#chHandle')).to_have_value(baseline['handle'])
            unchanged()
            case['input_data']={field:value}
            case['resource_ids']=[baseline['channelId']]
            case['steps']=['Existing owned channel → invalid '+field,'Save → visible validation error','API all metadata/branding unchanged','Reload → original name/handle and API data restored']
        suite.case(owner,'LIVE-CHANNEL-INVALID-'+field.upper(),'U-CHANNEL.02','Channel rejects '+field,'Invalid field does not persist or alter any existing channel metadata',invalid,module='channel')
    for index,(kind,max_mb) in enumerate([('avatar',5),('banner',10),('watermark',2)]):
        for invalid_kind in ['mime','empty','size']:
            def reject(case,index=index,kind=kind,max_mb=max_mb,invalid_kind=invalid_kind):
                suite.go(owner,route,'app-channel-settings-page')
                page.locator('.settings-tabs-nav .tab-btn').nth(1).click()
                payload={'name':'qc-invalid.txt','mimeType':'text/plain','buffer':b'QC invalid branding'} if invalid_kind=='mime' else {'name':'qc-invalid.png','mimeType':'image/png','buffer':b'' if invalid_kind=='empty' else b'0'*(max_mb*1024*1024+1)}
                action=lambda:page.locator('.branding-tab-pane input[type=file]').nth(index).set_input_files(payload)
                if kind=='avatar':
                    action()
                    expect(page.locator('app-result-notice[tone=error]')).to_be_visible()
                    expect(page.locator('app-channel-avatar-cropper')).to_have_count(0)
                    suite.api(owner,'post',path+'/'+kind,multipart={'file':payload},expected=(400,413,415,422))
                elif invalid_kind=='size':
                    # Kestrel's request-size rejection can lack CORS headers;
                    # the browser then reports requestfailed rather than response.
                    action()
                    expect(page.locator('app-result-notice[tone=error]')).to_be_visible()
                    suite.api(owner,'post',path+'/'+kind,multipart={'file':payload},expected=(400,413,415,422))
                else:
                    suite.response(owner,path+'/'+kind,action,method='POST',status=(400,413,415,422))
                expect(page.locator('app-result-notice[tone=error]')).to_be_visible()
                unchanged()
                suite.evidence(owner,case,'branding-error')
                page.reload(); expect(page.locator('#chName')).to_have_value(baseline['name'])
                unchanged()
                case['input_data']={'file_name':payload['name'],'mime':payload['mimeType'],'bytes':len(payload['buffer'])}
                case['resource_ids']=[baseline['channelId']]
                case['steps']=['Branding → real '+kind+' file input '+invalid_kind,'Assert actual rejection HTTP and visible error','All channel URLs/metadata unchanged','Reload → original persisted channel']
            suite.case(owner,'LIVE-CHANNEL-'+kind.upper()+'-INVALID-'+invalid_kind.upper(),'U-CHANNEL.02',kind+' rejects '+invalid_kind,'Invalid branding upload is rejected and leaves original channel data unchanged',reject,module='channel')
