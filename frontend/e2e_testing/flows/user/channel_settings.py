"""Validate/persist basic settings and real PNG branding in isolated fixtures."""
from support.avatar_crop import confirm_avatar_crop
import json
import struct
import zlib
from playwright.sync_api import expect


def png(width, height):
    def chunk(kind, data):
        return struct.pack('!I', len(data)) + kind + data + struct.pack('!I', zlib.crc32(kind + data) & 0xffffffff)
    rows = b''.join(b'\x00' + bytes([30, 95, 145]) * width for _ in range(height))
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('!IIBBBBB', width, height, 8, 2, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b''))


def run(suite, owner, member):
    page = owner['page']
    baseline = suite.api(owner, 'get', '/channels/me').json()
    base = '/channels/' + baseline['channelId']
    route = '/channel/' + baseline['handle'] + '/customize'
    recovery = suite.state.get('channel_basic_recovery')
    if recovery:
        values = {key: value for key, value in recovery.items() if key != 'contactEmail'}
        assert suite.api(owner, 'patch', base, data=values).status == 200
        suite.db.sql("UPDATE channels SET contact_email=NULLIF(:'email','') WHERE channel_id=:'id'::uuid AND owner_user_id=:'owner'::uuid;",
                     {'email': recovery.get('contactEmail') or '', 'id': baseline['channelId'], 'owner': owner['user_id']})
        baseline = suite.api(owner, 'get', '/channels/me').json()
    elif baseline.get('contactEmail') == 'hutube.e2e.contact@example.test':
        # Repair the fixture left by the earlier null/empty PATCH test.
        suite.db.sql("UPDATE channels SET contact_email=NULL WHERE channel_id=:'id'::uuid AND owner_user_id=:'owner'::uuid;",
                     {'id': baseline['channelId'], 'owner': owner['user_id']})
        baseline = suite.api(owner, 'get', '/channels/me').json()
    suite.state['channel_basic_recovery'] = {
        key: baseline.get(key) or ('' if key in ('description', 'contactEmail') else baseline.get(key))
        for key in ('name', 'handle', 'description', 'contactEmail', 'settings')}
    suite.save()

    def open_basic():
        suite.go(owner, route, 'app-channel-settings-page')
        expect(page.locator('#chName')).to_have_value(baseline['name'])

    def validation():
        open_basic()
        invalid = suite.api(owner, 'patch', base, data={'contactEmail': 'invalid-email'})
        assert invalid.status == 400
        assert suite.api(owner, 'get', '/channels/me').json().get('contactEmail') == baseline.get('contactEmail')
        page.locator('#chName').fill('   ')
        page.locator('.basic-tab-pane .form-actions button').click()
        expect(page.locator('app-result-notice[tone=error]')).to_be_visible()
        assert suite.api(owner, 'get', '/channels/me').json()['name'] == baseline['name']
        page.locator('#chName').fill(baseline['name'])
        previous_links = page.locator('.link-row-card').count()
        page.locator('.btn-add-link').click()
        expect(page.locator('.link-row-card')).to_have_count(previous_links + 1)
        page.locator('.link-row-card').last.locator('input[type=url]').fill('javascript:alert(1)')
        page.locator('.basic-tab-pane .form-actions button').click()
        expect(page.locator('.link-field-box.has-error')).to_be_visible()
        assert suite.api(owner, 'get', '/channels/me').json().get('settings') == baseline.get('settings')
    suite.case(owner, 'U-CHANNEL-VALIDATION-01', 'Tên trắng và URL javascript không persist', validation,
               expected='UI lỗi; API giữ tên/settings gốc; URL chỉ chấp nhận HTTP(S)')

    try:
        def save():
            open_basic()
            page.locator('#chDesc').fill('  HuTube E2E — thông tin kênh và nguồn học tập  ')
            page.locator('#chEmail').fill('hutube.e2e.contact@example.test')
            while page.locator('.link-row-card').count():
                previous_links = page.locator('.link-row-card').count()
                page.locator('.btn-remove-link').last.click()
                expect(page.locator('.link-row-card')).to_have_count(previous_links - 1)
            page.locator('.btn-add-link').click()
            expect(page.locator('.link-row-card')).to_have_count(1)
            page.locator('#link-platform-0').select_option('other')
            page.locator('#link-url-0').fill('https://example.test/hutube-learning')
            with page.expect_response(lambda response: response.request.method == 'PATCH' and response.url.endswith(base)) as pending:
                page.locator('.basic-tab-pane .form-actions button').click()
            assert pending.value.status == 200
            open_basic()
            expect(page.locator('#chDesc')).to_have_value('HuTube E2E — thông tin kênh và nguồn học tập')
            expect(page.locator('#link-url-0')).to_have_value('https://example.test/hutube-learning')
            stored = suite.api(owner, 'get', '/channels/me').json()
            assert stored['description'] == 'HuTube E2E — thông tin kênh và nguồn học tập'
            settings = json.loads(stored['settings']) if isinstance(stored['settings'], str) else stored['settings']
            assert len(settings['links']) == 1 and settings['links'][0]['platform'] == 'other'
            denied = suite.api(member, 'patch', base, data={'name': 'Forbidden name'})
            assert denied.status == 403
        suite.case(owner, 'U-CHANNEL-BASIC-01', 'Lưu mô tả/contact/link; reload; chặn non-member', save,
                   expected='PATCH 200; trim Unicode và links persist; member PATCH 403')
        def clear_contact():
            open_basic()
            page.locator('#chEmail').fill('')
            with page.expect_response(lambda response: response.request.method == 'PATCH' and response.url.endswith(base)) as pending:
                page.locator('.basic-tab-pane .form-actions button').click()
            assert pending.value.status == 200
            open_basic()
            expect(page.locator('#chEmail')).to_have_value('')
            assert suite.api(owner, 'get', '/channels/me').json().get('contactEmail') is None
        suite.case(owner, 'U-CHANNEL-CONTACT-CLEAR-01', 'Xóa email liên hệ qua UI và reload', clear_contact,
                   expected='PATCH 200 cho contact rỗng; API trả null; form sau reload giữ rỗng')
    finally:
        def restore():
            values = {key: baseline.get(key) for key in ('name', 'handle', 'description', 'contactEmail', 'settings')}
            # PATCH null means "unchanged"; an empty optional string clears it.
            for key in ('description', 'contactEmail'):
                values[key] = values[key] or ''
            assert suite.api(owner, 'patch', base, data=values).status == 200
            stored = suite.api(owner, 'get', '/channels/me').json()
            assert all(stored.get(key) == baseline.get(key) for key in values)
            page.evaluate('(id) => localStorage.removeItem("hutube_channel_links_" + id)', baseline['channelId'])
            open_basic()
            suite.state.pop('channel_basic_recovery', None)
            suite.save()
        restored = suite.case(owner, 'U-CHANNEL-BASIC-RESTORE-01', 'Phục hồi metadata/link fixture sau test', restore,
                              expected='PATCH cho phép xóa contact; GET giữ đúng metadata ban đầu')
        if not restored:
            # Keep the application failure in the report. SQL is only cleanup
            # of the managed fixture; it is never counted as API/UI success.
            values = {key: baseline.get(key) for key in ('name', 'handle', 'description', 'settings')}
            assert suite.api(owner, 'patch', base, data=values).status == 200
            suite.db.sql("UPDATE channels SET contact_email=NULLIF(:'email','') WHERE channel_id=:'id'::uuid AND owner_user_id=:'owner'::uuid;",
                         {'email': baseline.get('contactEmail') or '', 'id': baseline['channelId'], 'owner': owner['user_id']})
            suite.report.setdefault('fixture_cleanup', []).append({'resource': 'channel', 'method': 'managed-db', 'reason': 'API could not clear optional contact; original failure retained'})
            suite.state.pop('channel_basic_recovery', None)
            suite.save()

    for index, (kind, dimensions, field, selector) in enumerate([
        ('avatar', (256, 256), 'avatarUrl', '.avatar-preview img'),
        ('banner', (1280, 720), 'bannerUrl', '.banner-preview img'),
        ('watermark', (150, 150), 'watermarkUrl', '.watermark-preview-stage img'),
    ]):
        def branding(index=index, kind=kind, dimensions=dimensions, field=field, selector=selector):
            open_basic()
            page.locator('.tab-btn').filter(has_text='Xây dựng thương hiệu').click()
            with page.expect_response(lambda response: response.request.method == 'POST' and response.url.endswith(base + '/' + kind)) as pending:
                page.locator('.branding-tab-pane input[type=file]').nth(index).set_input_files(
                    {'name': 'hutube-e2e-' + kind + '.png', 'mimeType': 'image/png', 'buffer': png(*dimensions)})
                if kind == 'avatar': confirm_avatar_crop(page)
            assert pending.value.status == 200
            value = suite.api(owner, 'get', '/channels/me').json()[field]
            assert value
            open_basic()
            page.locator('.tab-btn').filter(has_text='Xây dựng thương hiệu').click()
            expect(page.locator(selector)).to_be_visible()
            expect(page.locator(selector)).to_have_js_property('complete', True)
            assert page.locator(selector).evaluate('(image) => image.complete && image.naturalWidth > 0')
        suite.case(owner, 'U-CHANNEL-' + kind.upper() + '-01', 'Upload PNG ' + kind + ' và reload preview', branding,
                   expected='Multipart POST 200; URL persist; ảnh tải thật có naturalWidth > 0',
                   data=f'Synthetic PNG {dimensions[0]}x{dimensions[1]}; giữ branding trên fixture E2E')
