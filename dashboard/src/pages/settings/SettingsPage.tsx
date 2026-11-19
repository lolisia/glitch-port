import { Monitor, Moon, Sun } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from '@/components/ui/card';
import { Label } from '@/components/ui/label';
import { NativeSelect, NativeSelectOption } from '@/components/ui/native-select';
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group';
import { isLocale, useI18n } from '../../i18n';
import { useTheme } from '../../theme/ThemeProvider';
import { isThemePreference, type ThemePreference } from '../../theme/theme';

interface ThemeOption {
  value: ThemePreference;
  labelKey: string;
  icon: LucideIcon;
}

const THEME_OPTIONS: ThemeOption[] = [
  {
    value: 'system',
    labelKey: 'settings.appearance.system',
    icon: Monitor,
  },
  { value: 'light', labelKey: 'settings.appearance.light', icon: Sun },
  { value: 'dark', labelKey: 'settings.appearance.dark', icon: Moon },
];

/**
 * #/settings: appearance and language preferences. Both controls apply and
 * persist immediately through the existing providers, so the screen has no
 * save button.
 */
export function SettingsPage() {
  const { t, locale, setLocale } = useI18n();
  const { preference, setPreference } = useTheme();

  const selectTheme = (value: string) => {
    if (isThemePreference(value)) {
      setPreference(value);
    }
  };

  const selectLocale = (value: string) => {
    if (isLocale(value)) {
      setLocale(value);
    }
  };

  return (
    <section data-testid="settings-page" className="space-y-6">
      <div className="space-y-1">
        <h2 className="text-2xl font-semibold tracking-tight">
          {t('settings.title')}
        </h2>
        <p className="text-sm text-muted-foreground">
          {t('settings.description')}
        </p>
      </div>

      <div className="grid gap-6 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>{t('settings.appearance.title')}</CardTitle>
            <CardDescription>
              {t('settings.appearance.description')}
            </CardDescription>
          </CardHeader>
          <CardContent>
            <RadioGroup
              aria-label={t('common.theme')}
              value={preference}
              onValueChange={selectTheme}
            >
              {THEME_OPTIONS.map((option) => {
                const Icon = option.icon;
                const id = `theme-${option.value}`;
                const label = t(option.labelKey);

                return (
                  <div key={option.value} className="flex items-center gap-3">
                    <RadioGroupItem
                      id={id}
                      value={option.value}
                      aria-label={label}
                    />
                    <Label htmlFor={id}>
                      <Icon className="size-4" aria-hidden="true" />
                      {label}
                    </Label>
                  </div>
                );
              })}
            </RadioGroup>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t('settings.language.title')}</CardTitle>
            <CardDescription>
              {t('settings.language.description')}
            </CardDescription>
          </CardHeader>
          <CardContent>
            <NativeSelect
              aria-label="Language"
              value={locale}
              onChange={(event) => selectLocale(event.target.value)}
              className="w-48"
            >
              <NativeSelectOption value="en">English</NativeSelectOption>
              <NativeSelectOption value="ko">한국어</NativeSelectOption>
            </NativeSelect>
          </CardContent>
        </Card>
      </div>
    </section>
  );
}
