import {
  Bug,
  FolderKanban,
  MessageSquare,
  Settings,
  Tag,
} from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import logoUrl from '../assets/glitch-port-icon.svg';
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
} from '@/components/ui/sidebar';
import { useI18n } from '../i18n';
import type { AppRoute } from '../router';

interface SidebarNavItem {
  href: string;
  /** Dotted i18n key resolved at render time. */
  labelKey: string;
  icon: LucideIcon;
  isActive: boolean;
}

/**
 * The navigation sidebar. The links are plain `<a href="#/...">` elements so
 * the hash router keeps handling navigation (and back/forward) exactly as it
 * did when the tabs lived in the top bar.
 */
export function AppSidebar({ route }: { route: AppRoute }) {
  const { t } = useI18n();

  // Unknown routes fall back to the issues screen, so Issues stays
  // highlighted until the normalising effect replaces the hash.
  const unknownRoute = route.name === 'unknown';

  const items: SidebarNavItem[] = [
    {
      href: '#/issues',
      labelKey: 'nav.issues',
      icon: Bug,
      isActive:
        route.name === 'issues' || route.name === 'issue' || unknownRoute,
    },
    {
      href: '#/releases',
      labelKey: 'nav.releases',
      icon: Tag,
      isActive: route.name === 'releases',
    },
    {
      href: '#/messages',
      labelKey: 'nav.messages',
      icon: MessageSquare,
      isActive: route.name === 'messages',
    },
    {
      href: '#/projects',
      labelKey: 'nav.projects',
      icon: FolderKanban,
      isActive: route.name === 'projects' || route.name === 'project',
    },
  ];

  const settingsActive = route.name === 'settings';

  return (
    <Sidebar collapsible="icon">
      <SidebarHeader>
        <a
          href="#/issues"
          className="flex items-center gap-2 rounded-md p-2 text-sidebar-foreground hover:bg-sidebar-accent hover:text-sidebar-accent-foreground"
        >
          <img
            src={logoUrl}
            alt=""
            width={28}
            height={28}
            className="size-7 shrink-0"
          />
          <h1 className="truncate text-base font-semibold group-data-[collapsible=icon]:hidden">
            glitch-port
          </h1>
        </a>
      </SidebarHeader>

      <SidebarContent>
        <nav aria-label={t('nav.sections')}>
          <SidebarGroup>
            <SidebarGroupLabel>{t('nav.sections')}</SidebarGroupLabel>
            <SidebarGroupContent>
              <SidebarMenu>
                {items.map((item) => {
                  const Icon = item.icon;

                  return (
                    <SidebarMenuItem key={item.href}>
                      <SidebarMenuButton
                        asChild
                        isActive={item.isActive}
                        tooltip={t(item.labelKey)}
                      >
                        <a
                          href={item.href}
                          aria-current={
                            item.isActive ? 'page' : undefined
                          }
                        >
                          <Icon aria-hidden="true" />
                          <span>{t(item.labelKey)}</span>
                        </a>
                      </SidebarMenuButton>
                    </SidebarMenuItem>
                  );
                })}
              </SidebarMenu>
            </SidebarGroupContent>
          </SidebarGroup>
        </nav>
      </SidebarContent>

      <SidebarFooter>
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuButton
              asChild
              isActive={settingsActive}
              tooltip={t('nav.settings')}
            >
              <a
                href="#/settings"
                aria-current={settingsActive ? 'page' : undefined}
              >
                <Settings aria-hidden="true" />
                <span>{t('nav.settings')}</span>
              </a>
            </SidebarMenuButton>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarFooter>
    </Sidebar>
  );
}
