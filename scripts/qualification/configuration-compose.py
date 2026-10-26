#!/usr/bin/env python3
"""Resolve supported application Compose overlays with redacted disposable inputs."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

root = Path(__file__).resolve().parents[2]
env = {k:v for k,v in os.environ.items() if k in ('PATH','HOME','DOCKER_HOST','DOCKER_CONTEXT','DOCKER_CONFIG')}
with tempfile.TemporaryDirectory(prefix='agent-core-compose-config-') as temporary:
    temporary=Path(temporary); certificate=temporary/'fixture.pfx'; certificate.write_bytes(b'configuration-only-fixture')
    inputs=temporary/'fixture.env'; inputs.write_text(f'GOOGLE_OAUTH_CLIENT_ID=fixture-client\nGOOGLE_OAUTH_CLIENT_SECRET=fixture-secret\nGOOGLE_OAUTH_PUBLIC_APP_URL=https://app.example.test\nAGENTCORE_DRIVE_ENABLED=false\nAGENTCORE_TLS_CERTIFICATE_PATH={certificate}\nAGENTCORE_TLS_PASSWORD=fixture-password\n')
    outcomes=[]
    for overlays in ([], ['docker-compose.real.yml'], ['docker-compose.https.yml'], ['docker-compose.real.yml','docker-compose.https.yml']):
        args=[sys.executable,'scripts/compose.py','--env-file',str(inputs),'-f','docker-compose.yml']
        for overlay in overlays:args+=['-f',overlay]
        value=subprocess.run([*args,'config','--format','json'],cwd=root,env=env,capture_output=True,text=True,check=True)
        service=json.loads(value.stdout)['services']['agent-core']; settings=service['environment']
        assert settings['GoogleDrive__Enabled']=='false'
        assert settings['AgentCore__Profile']==('Real' if 'docker-compose.real.yml' in overlays else 'Synthetic')
        assert not any('GoogleEmailOAuth' in key or 'GoogleDriveOAuth' in key or key.startswith('GOOGLE_') for key in settings)
        if 'docker-compose.real.yml' in overlays:assert settings['GoogleOAuth__ClientId']=='fixture-client'
        outcomes.append({'overlays':overlays,'canonicalResolution':True})
    for bad in ('GOOGLE_EMAIL_OAUTH_CLIENT_SECRET=private-sentinel','FilePreview__Limits__CsvBytes=123','GOOGLE_OAUTH_CLIENT_SECRET=private-sentinel\nGoogleOAuth__ClientSecret=private-sentinel'):
        inputs.write_text(bad+'\n'); result=subprocess.run([sys.executable,'scripts/compose.py','--env-file',str(inputs),'-f','docker-compose.yml','config'],cwd=root,env=env,capture_output=True,text=True)
        assert result.returncode and 'private-sentinel' not in result.stderr+result.stdout
    print(json.dumps({'resolved':outcomes,'retiredAndAmbiguousInputsRefused':3}))
