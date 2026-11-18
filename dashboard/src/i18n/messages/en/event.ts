import type { DeepStringify } from '../types';

/** English messages for the event body shown under an issue. */
export const event = {
  key: 'Key',
  value: 'Value',
  exceptions: 'Exceptions',
  message: 'Message',
  tags: 'Tags',
  user: 'User',
  contexts: 'Contexts',
  breadcrumbs: 'Breadcrumbs',
  sdk: 'SDK',
  extra: 'Extra',
  rawJson: 'Raw JSON',
  data: 'data',
  unknownFrame: '<unknown>',
  unknownSdk: 'unknown',
  showSystemFrames_one: 'Show {count} system frames',
  showSystemFrames_other: 'Show {count} system frames',
  hideSystemFrames_one: 'Hide {count} system frames',
  hideSystemFrames_other: 'Hide {count} system frames',
  userFields: {
    id: 'ID',
    username: 'Username',
    email: 'Email',
    ipAddress: 'IP address',
  },
} as const;

export type EventMessages = DeepStringify<typeof event>;
