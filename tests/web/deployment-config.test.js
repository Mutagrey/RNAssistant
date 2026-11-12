'use strict';
// Real settings view; signature verification is covered by the host-neutral policy fixture.
const assert = require('node:assert/strict');
const {withPreviewPage} = require('./preview-session');
(async () => {
  await withPreviewPage(async ({page, errors}) => {
    await page.setViewportSize({width:1280, height:900});
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(() => {
      const original = send;
      window.deploymentFixture = {calls:[], delay:false};
      const status = {state:'Applied'};
      window.send = (type, payload, ...rest) => {
        if (!['getDeploymentConfigStatus','unlockDeveloperMode','saveDeploymentConfig','lockDeveloperMode','openDeploymentConfigDirectory'].includes(type)) return original(type, payload, ...rest);
        deploymentFixture.calls.push({type, ...structuredClone(payload)});
        if (type === 'getDeploymentConfigStatus') return Promise.resolve({...status});
        if (type === 'lockDeveloperMode') return Promise.resolve({locked:true});
        if (type === 'openDeploymentConfigDirectory') return Promise.resolve({opened:true});
        const editor = {sessionToken:'runtime-token', expiresUtc:new Date(Date.now() + 600000).toISOString(), sites:payload.sites || ['https://*.example.com'], allowAnyManualSite:!!payload.allowAnyManualSite, policyVersion:1, filePath:'C:\\Users\\Developer\\AppData\\Roaming\\RNAssistant\\deployment-config.bin', privateKeyPath:payload.privateKeyPath || 'C:\\repo\\.local\\deployment\\signing-key.bin', status};
        if (deploymentFixture.delay) { deploymentFixture.delay = false; return new Promise(resolve => { deploymentFixture.release = () => resolve(editor); }); }
        return Promise.resolve(editor);
      };
      RNAssistantDockWorkspace.show('settings');
      window.deploymentView = document.querySelector('[data-deployment-config]');
    });
    await page.locator('#deploymentConfigStatus').getByText('Конфигурация загружена').waitFor();
    assert.equal(await page.locator('#deploymentConfigEditor').isVisible(), false);
    assert.equal(await page.locator('#deploymentConfigSites').inputValue(), '');
    assert.equal(await page.locator('#deploymentConfigPath').isVisible(), false, 'ordinary status does not show configuration details');
    assert.equal(await page.locator('#browserAgentSitesInput').count(), 0, 'ordinary editable grants removed');
    await page.locator('#deploymentConfigDeveloper > summary').click();
    await page.locator('#deploymentConfigPassword').fill('fixture-code');
    await page.locator('#deploymentConfigPassword').press('Enter');
    await page.locator('#deploymentConfigSites').waitFor({state:'visible'});
    assert.equal(await page.locator('#deploymentConfigPassword').inputValue(), '', 'code cleared after submission');
    assert.equal(await page.locator('#deploymentConfigKeyPath').inputValue(), 'C:\\repo\\.local\\deployment\\signing-key.bin', 'editor starts with the build-time key path');
    await page.locator('#deploymentConfigSites').fill('https://example.org\n\n https://*.example.com ');
    await page.locator('#deploymentConfigKeyPath').fill('C:\\private\\signing-key.bin');
    await page.locator('#deploymentConfigManual').check();
    await page.locator('#deploymentConfigOpenDirectory').click();
    await page.locator('#deploymentConfigSave').click();
    await page.locator('#deploymentConfigEditorStatus').getByText('Конфигурация сохранена и проверена.', {exact:false}).waitFor();
    assert.deepEqual(await page.evaluate(() => deploymentFixture.calls.find(c => c.type === 'saveDeploymentConfig').sites), ['https://example.org','https://*.example.com']);
    assert.ok(await page.evaluate(() => deploymentFixture.calls.find(c => c.type === 'saveDeploymentConfig').allowAnyManualSite));
    assert.ok(await page.evaluate(() => deploymentFixture.calls.some(c => c.type === 'openDeploymentConfigDirectory' && c.sessionToken === 'runtime-token')));
    assert.equal(await page.evaluate(() => settingsDirty), false, 'developer edits do not enter ordinary settings save');
    const sizes = await page.evaluate(() => ['baseUrlInput','deploymentConfigPassword','deploymentConfigPath'].map(id => getComputedStyle(document.getElementById(id)).fontSize));
    assert.equal(sizes[0], sizes[1]); assert.equal(sizes[0], sizes[2]);
    if (process.env.DEPLOYMENT_CONFIG_REVIEW) await page.screenshot({path:process.env.DEPLOYMENT_CONFIG_REVIEW + '-wide.png'});
    await page.setViewportSize({width:420, height:900});
    await page.evaluate(() => { document.documentElement.style.zoom = '1.25'; });
    await page.locator('#deploymentConfigManual').scrollIntoViewIfNeeded();
    assert.ok(await page.locator('[data-deployment-config]').evaluate(el => el.scrollWidth <= el.clientWidth + 1), 'shared controls fit narrow 125%');
    if (process.env.DEPLOYMENT_CONFIG_REVIEW) await page.screenshot({path:process.env.DEPLOYMENT_CONFIG_REVIEW + '-narrow.png'});
    await page.evaluate(() => RNAssistantDockWorkspace.show('conversation'));
    assert.equal(await page.evaluate(() => deploymentView.querySelector('#deploymentConfigSites').value), '', 'leaving settings clears sensitive list');
    assert.ok(await page.evaluate(() => deploymentFixture.calls.some(c => c.type === 'lockDeveloperMode')));
    await page.evaluate(() => { RNAssistantDockWorkspace.show('settings'); deploymentFixture.delay = true; });
    await page.locator('#deploymentConfigPassword').fill('fixture-code');
    await page.locator('#deploymentConfigUnlock').click();
    await page.waitForFunction(() => !!deploymentFixture.release);
    await page.evaluate(() => { RNAssistantDockWorkspace.show('conversation'); deploymentFixture.release(); });
    await page.waitForFunction(() => !deploymentView.querySelector('#deploymentConfigUnlock').disabled);
    assert.equal(await page.evaluate(() => deploymentView.querySelector('#deploymentConfigSites').value), '', 'late unlock never restores hidden rules');
    assert.deepEqual(errors, []);
    console.log('PASS Deployment configuration: shared settings, signing command, clearing and late unlock guards, narrow/125%');
  });
})().catch(error => { console.error(error); process.exitCode = 1; });
