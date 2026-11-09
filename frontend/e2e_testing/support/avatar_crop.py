"""Confirm the real channel avatar crop dialog before expecting an upload."""
from playwright.sync_api import expect


def confirm_avatar_crop(page):
    dialog = page.locator('app-channel-avatar-cropper dialog:modal')
    expect(dialog).to_be_visible()
    expect(dialog.locator('.apply-button')).to_be_enabled()
    dialog.locator('.apply-button').click()
    expect(dialog).to_have_count(0)
