// Ambient shim for Intl.DurationFormat — Baseline 2024, Chrome 129+,
// available in our Electron Chromium but not yet in TypeScript's ES2022
// lib. Declare just the shape we use (locale + style + format); the rest
// of the spec stays out of scope until a consumer needs it.
declare namespace Intl {
  interface DurationFormatOptions {
    style?: 'long' | 'short' | 'narrow' | 'digital';
    years?: 'long' | 'short' | 'narrow' | 'numeric' | '2-digit';
    months?: 'long' | 'short' | 'narrow' | 'numeric' | '2-digit';
    weeks?: 'long' | 'short' | 'narrow' | 'numeric' | '2-digit';
    days?: 'long' | 'short' | 'narrow' | 'numeric' | '2-digit';
    hours?: 'long' | 'short' | 'narrow' | 'numeric' | '2-digit';
    minutes?: 'long' | 'short' | 'narrow' | 'numeric' | '2-digit';
    seconds?: 'long' | 'short' | 'narrow' | 'numeric' | '2-digit';
    milliseconds?: 'long' | 'short' | 'narrow' | 'numeric' | '2-digit';
  }

  interface DurationInput {
    years?: number;
    months?: number;
    weeks?: number;
    days?: number;
    hours?: number;
    minutes?: number;
    seconds?: number;
    milliseconds?: number;
    microseconds?: number;
    nanoseconds?: number;
  }

  class DurationFormat {
    constructor(locales?: string | string[], options?: DurationFormatOptions);
    format(duration: DurationInput): string;
  }
}
