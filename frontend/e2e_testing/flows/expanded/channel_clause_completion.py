"""Previously unexecuted channel validation, partial failure and deleted-route branches."""
from support.avatar_crop import confirm_avatar_crop
import time
from urllib.parse import urlsplit, parse_qs
from playwright.sync_api import expect
from flows.expanded.common import case, snapshot_module
from flows.expanded.channels import png_bytes
from tests.full_functional import pickup_link


def run(suite, config, actors, only_failed=False):
    snapshot_module(suite, __file__)
    key = 'channel_clause'
    stamp = str(time.time_ns())
    suite.credentials[key] = {'email': 'qc.channel.' + stamp + '@example.test',
                              'username': 'qc_ch_' + stamp, 'password': 'QCFixturePassword2026!'}
    credentials = suite.credentials[key]
    setup = suite.actor('channel-clause-setup', 'user', 'guest')
    setup['api_url'] = config['api_url']
    sent = time.time()
    suite.api(setup, 'post', '/auth/register', data={**credentials, 'displayName': 'QC channel clauses'}, expected=201)
    link = pickup_link(config, credentials['email'], 'verify-email', sent)
    token = parse_qs(urlsplit(link).query)['token'][0]
    suite.api(setup, 'post', '/auth/verify-email', data={'token': token})
    suite.release_actor(setup)
    actor = suite.actor('channel-clause-owner', 'user', key)
    p = actor['page']
    created = {}
    handle = 'qc-clause-' + stamp
    try:
        def validation(entry):
            suite.go(actor, '/channel/create', 'app-channel-create-page')
            expect(p.locator('#channelName')).to_be_visible()
            p.locator('#channelName').fill('   ')
            p.locator('#channelHandle').fill(handle)
            expect(p.locator('button[type=submit]')).to_be_disabled()
            p.locator('#channelName').fill('  Kênh kiểm thử chuẩn hóa  ')
            for invalid in ['ab', 'bad handle', 'bad/handle', 'a' * 51]:
                p.locator('#channelHandle').fill(invalid)
                expect(p.locator('.feedback-text.is-invalid')).to_be_visible()
                expect(p.locator('button[type=submit]')).to_be_disabled()
                suite.evidence(actor, entry, 'invalid-handle-' + str(len(invalid)))
            p.locator('#channelHandle').fill('  @@' + handle + '  ')
            p.locator('#channelHandle').press('Tab')
            expect(p.locator('#channelHandle')).to_have_value(handle)
            expect(p.locator('.feedback-text.is-valid')).to_be_visible()
            expect(p.locator('button[type=submit]')).to_be_enabled()
            entry['steps'] = ['Whitespace-only name disables create', 'Short, whitespace, invalid-character and overlong handles reject without creating a channel', 'Leading @ and surrounding whitespace normalize on blur; valid availability comes from real API']
        if not only_failed:
            case(suite, actor, 'CHANNEL-CREATE-INVALID-NORMALIZATION', 'U-CHANNEL.01', 'Channel create rejects invalid handles and normalizes whitespace', validation, 'channel')

        def partial(entry):
            if only_failed:
                suite.go(actor, '/channel/create', 'app-channel-create-page')
                p.locator('#channelHandle').fill(handle)
                expect(p.locator('.feedback-text.is-valid')).to_be_visible()
            p.locator('#channelName').fill('  Kênh kiểm thử chuẩn hóa  ')
            p.locator('#channelDescription').fill('  Mô tả chuẩn hóa  ')
            p.locator('.avatar-overlap-box input[type=file]').set_input_files({'name': 'avatar.png', 'mimeType': 'image/png', 'buffer': png_bytes()})
            confirm_avatar_crop(p)
            p.locator('.banner-box input[type=file]').set_input_files({'name': 'banner.txt', 'mimeType': 'text/plain', 'buffer': b'invalid actual image'})
            with p.expect_response(lambda r: r.request.method == 'POST' and r.url.endswith('/banner')) as banner:
                response = suite.response(actor, '/channels', lambda: p.locator('button[type=submit]').click(), method='POST', status=201)
            created.update(response.json())
            assert banner.value.status == 400
            saved = suite.api(actor, 'get', '/channels/' + created['channelId']).json()
            assert saved['name'] == 'Kênh kiểm thử chuẩn hóa' and saved['description'] == 'Mô tả chuẩn hóa'
            assert saved['handle'] == handle
            assert saved['avatarUrl'] and not saved.get('bannerUrl')
            expect(p.locator('app-channel-create-page [role=alert]')).to_be_visible()
            expect(p.locator('app-channel-page')).to_have_count(0)
            expect(p.locator('.existing-channel-card')).to_contain_text(saved['name'])
            suite.evidence(actor, entry, 'partial-upload-explicit-error')
            p.locator('.card-actions .btn-secondary').click()
            expect(p.locator('app-channel-settings-page')).to_be_visible()
            p.locator('.settings-tabs-nav .tab-btn').nth(1).click()
            retry = suite.response(actor, '/channels/' + created['channelId'] + '/banner', lambda: p.locator('.branding-card input[type=file]').nth(1).set_input_files({'name': 'banner-retry.png', 'mimeType': 'image/png', 'buffer': png_bytes()}), method='POST')
            current = suite.api(actor, 'get', '/channels/' + created['channelId']).json()
            assert current['bannerUrl'] == retry.json()['bannerUrl'] and current['avatarUrl'] == saved['avatarUrl']
            p.reload()
            expect(p.locator('app-channel-settings-page')).to_be_visible()
            p.locator('.settings-tabs-nav .tab-btn').nth(1).click()
            p.locator('.branding-card').nth(1).locator('img').evaluate('(img)=>img.decode()')
            entry['resource_ids'] = [created['channelId']]
            entry['steps'] = ['Create normalized channel with real valid avatar and invalid banner', 'Actual channel creation 201, avatar upload persists, banner rejects 400', 'Partial upload failure remains visible without unconditional successful navigation', 'Read persisted channel fields and exact successful/failed branding assets']
        case(suite, actor, 'CHANNEL-CREATE-PARTIAL-BRANDING-FAILURE', 'U-CHANNEL.02', 'Partial branding failure cannot silently report complete creation', partial, 'channel')

        if created and not only_failed:
            def deleted(entry):
                cid = created['channelId']
                suite.api(actor, 'delete', '/channels/' + cid, expected=(200, 204))
                response = suite.response(actor, '/channels/handle/' + handle, lambda: p.goto(actor['origin'] + '/channel/' + handle), status=404)
                assert response.status == 404
                expect(p.locator('.channel-error-state')).to_be_visible()
                expect(p.locator('.channel-video-card,.channel-playlist-card,.channel-name')).to_have_count(0)
                p.reload()
                expect(p.locator('.channel-error-state')).to_be_visible()
                entry['resource_ids'] = [cid]
                entry['steps'] = ['Delete owned temporary channel through actual API', 'Open its previously valid handle; actual 404, bounded error and no metadata/cards', 'Reload preserves deleted/notfound behavior']
            case(suite, actor, 'CHANNEL-ACTUAL-DELETED-ROUTE', 'U-CHANNEL.03', 'Deleted channel route exposes no stale metadata', deleted, 'channel')

        def loading(entry):
            owner_channel = suite.api(actors['owner'], 'get', '/channels/me').json()
            pattern = '**/api/v1/channels/handle/' + owner_channel['handle']
            held = []
            p.route(pattern, lambda route: held.append(route))
            try:
                p.goto(actor['origin'] + '/channel/' + owner_channel['handle'], wait_until='domcontentloaded')
                expect(p.locator('.channel-loading-skeleton')).to_be_visible()
                expect(p.locator('.channel-name')).to_have_count(0)
                suite.evidence(actor, entry, 'channel-loading-real-request-held')
                assert len(held) == 1
                held.pop().continue_()
                expect(p.locator('.channel-name')).to_contain_text(owner_channel['name'])
                expect(p.locator('.channel-loading-skeleton')).to_have_count(0)
                entry['resource_ids'] = [owner_channel['channelId']]
                entry['steps'] = ['Hold genuine channel request without fabricating response', 'Loading skeleton visible on three viewports; no stale channel identity', 'Continue actual request, real channel name appears and loading clears']
            finally:
                for route in held: route.continue_()
                p.unroute(pattern)
        if not only_failed:
            case(suite, actor, 'CHANNEL-LOADING-REAL-REQUEST', 'U-CHANNEL.03', 'Channel skeleton clears after genuine delayed request', loading, 'channel')
    finally:
        if created:
            suite.api(actor, 'delete', '/channels/' + created['channelId'], expected=(200, 204, 404))
        suite.release_actor(actor)
