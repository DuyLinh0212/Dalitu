// Match Angular's email rules while keeping this channel-only guard in the lazy bundle.
const CONTACT_EMAIL = /^(?=.{1,254}$)(?=.{1,64}@)[a-zA-Z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[a-zA-Z0-9!#$%&'*+/=?^_`{|}~-]+)*@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$/;

// Contact email is optional; validate the same trimmed value sent to the API.
export function validContactEmail(value: string): boolean {
  const email = value.trim();
  return !email || CONTACT_EMAIL.test(email);
}

export function avatarFileError(file: File): string | null {
  if (!file.size || !['image/jpeg', 'image/png', 'image/webp', 'image/gif'].includes(file.type)) {
    return 'channel.avatarFileError';
  }
  return file.size > 5 * 1024 * 1024 ? 'account.avatarSizeError' : null;
}
