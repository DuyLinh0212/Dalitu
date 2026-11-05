import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { of, Subject } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { ChannelService } from '../../core/channel.service';
import { ContentService } from '../../core/content.service';
import { suggestChannelHandle } from '../../core/channel-handle';
import { ChannelCreatePage } from './channel-create-page';
import { ChannelSettingsPage } from './channel-settings-page';
import { validContactEmail } from './channel-form-validation';

describe('Channel handle suggestions and edits', () => {
  let channels: jasmine.SpyObj<ChannelService>;
  beforeEach(() => {
    channels = jasmine.createSpyObj('channels', ['getMyChannel', 'getMyInvitations', 'getChannel', 'checkHandle', 'updateChannel', 'createChannel', 'getRoles']);
    channels.getMyChannel.and.returnValue(of(null as any));
    channels.getMyInvitations.and.returnValue(of([]));
    channels.getRoles.and.returnValue(of([]));
    channels.checkHandle.and.returnValue(of({ handle: 'custom', isAvailable: true, message: '' }));
    TestBed.configureTestingModule({ providers: [
      { provide: ChannelService, useValue: channels },
      { provide: AuthService, useValue: { user: signal({ displayName: 'Kí sự Jura', username: 'jura' }) } },
      { provide: ContentService, useValue: {} },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => 'lynwa' } } } },
      { provide: Router, useValue: { navigate: jasmine.createSpy().and.resolveTo(true), events: new Subject() } }
    ] });
  });
  it('suggests a valid name and only checks availability after a manual edit', fakeAsync(() => {
    TestBed.overrideComponent(ChannelCreatePage, { set: { template: '', imports: [] } });
    const fixture = TestBed.createComponent(ChannelCreatePage);
    const page = fixture.componentInstance;
    expect(page.handle).toMatch(/^ki-su-jura-[a-f0-9]{8}$/);
    page.onHandleBlur(); tick(300);
    expect(channels.checkHandle).not.toHaveBeenCalled();
    page.onNameInput('Đời sống');
    expect(page.handle).toMatch(/^doi-song-/);
    page.onHandleInput('my-custom-handle'); tick(300);
    expect(channels.checkHandle).toHaveBeenCalledOnceWith('my-custom-handle');
    page.onNameInput('Tên mới');
    expect(page.handle).toBe('my-custom-handle');
    fixture.destroy();
  }));
  it('keeps suggestions within the API limit, including names without Latin letters', () => {
    expect(suggestChannelHandle('a'.repeat(100), '12345678').length).toBe(50);
    expect(suggestChannelHandle('日本語', '12345678')).toBe('dalitu-12345678');
  });
  it('accepts optional and trimmed email addresses while rejecting URLs and malformed addresses', () => {
    for (const value of ['', '  ', ' contact+music@example.com ', 'artist@sub.example.com']) {
      expect(validContactEmail(value)).withContext(value).toBeTrue();
    }
    for (const value of ['facebook.com/OrinnMusic', 'https://example.com', 'artist@', '@example.com', 'a@@example.com', 'a b@example.com', 'a..b@example.com', 'a'.repeat(255)+'@example.com']) {
      expect(validContactEmail(value)).withContext(value).toBeFalse();
    }
  });
  it('blocks invalid contact email before creating a channel, including direct submission', () => {
    TestBed.overrideComponent(ChannelCreatePage, { set: { template: '', imports: [] } });
    const fixture = TestBed.createComponent(ChannelCreatePage);
    const page = fixture.componentInstance;
    page.contactEmail = 'facebook.com/OrinnMusic';
    page.onSubmit();
    expect(channels.createChannel).not.toHaveBeenCalled();
    expect(page.error()).toBeTruthy();
    page.contactEmail = ' contact@example.com ';
    channels.createChannel.and.returnValue(new Subject());
    page.onSubmit();
    expect(channels.createChannel.calls.mostRecent().args[0].contactEmail).toBe('contact@example.com');
    fixture.destroy();
  });
  it('blocks invalid contact email before saving an existing channel and still permits clearing it', () => {
    channels.getChannel.and.returnValue(of({ channelId: 'one', handle: 'lynwa', name: 'Channel', settings: '{}', permissions: ['channel.edit_profile'] } as any));
    TestBed.overrideComponent(ChannelSettingsPage, { set: { template: '', imports: [] } });
    const fixture = TestBed.createComponent(ChannelSettingsPage);
    const page = fixture.componentInstance;
    page.contactEmail = 'facebook.com/OrinnMusic';
    page.onSaveBasicInfo();
    expect(channels.updateChannel).not.toHaveBeenCalled();
    page.contactEmail = '';
    channels.updateChannel.and.returnValue(new Subject());
    page.onSaveBasicInfo();
    expect(channels.updateChannel.calls.mostRecent().args[1].contactEmail).toBe('');
    fixture.destroy();
  });
  it('omits an unchanged handle and excludes the current channel when checking edits', fakeAsync(() => {
    channels.getChannel.and.returnValue(of({ channelId: 'one', handle: 'lynwa', name: 'Channel', settings: '{}', permissions: ['channel.edit_profile'] } as any));
    TestBed.overrideComponent(ChannelSettingsPage, { set: { template: '', imports: [] } });
    const fixture = TestBed.createComponent(ChannelSettingsPage);
    const page = fixture.componentInstance;
    channels.updateChannel.and.returnValue(new Subject());
    page.onHandleChanged('@LYNWA'); tick(300);
    page.name = 'Kí sự Jura'; page.onSaveBasicInfo();
    expect(channels.checkHandle).not.toHaveBeenCalled();
    expect(channels.updateChannel.calls.mostRecent().args[1].handle).toBeUndefined();
    page.busy.set(false);
    page.onHandleChanged('jura-new'); tick(300);
    expect(channels.checkHandle).toHaveBeenCalledOnceWith('jura-new', 'one');
    fixture.destroy();
  }));
});
