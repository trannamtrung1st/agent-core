import { accountPages, tenantPages, instancePages as tenantInstancePages, systemPages } from '../access/experienceNavigation';
import { draftPages, instancePages, globalPages } from './adminNavigation';
import { navigationIconId, navigationIcons } from './AdminNavigationIcon';

it('gives every registered navigation destination an intentional semantic icon', () => {
  for (const page of [...accountPages, ...tenantPages, ...tenantInstancePages, ...systemPages, ...draftPages, ...instancePages, ...globalPages]) {
    expect(navigationIcons[navigationIconId(page.id)], page.id).toBeDefined();
  }
});
