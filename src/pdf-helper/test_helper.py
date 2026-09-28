import sys, json, subprocess, pathlib, datetime, io
from pypdf import PdfReader, PdfWriter
from reportlab.pdfgen import canvas
from cryptography import x509
from cryptography.x509.oid import NameOID
from cryptography.hazmat.primitives import hashes,serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.hazmat.primitives.serialization import pkcs12
root=pathlib.Path('artifacts/feature-tests').resolve();root.mkdir(parents=True,exist_ok=True)
source=root/'forms.pdf';c=canvas.Canvas(str(source),pagesize=(595,842));c.drawString(50,780,'Searchable document');c.acroForm.textfield(name='name',x=50,y=700,width=240,height=30);c.acroForm.checkbox(name='agree',x=50,y=650);c.bookmarkPage('start');c.addOutlineEntry('First page','start');c.showPage();c.drawString(50,780,'Second searchable page');c.save()
helper=pathlib.Path('artifacts/helper/airyview-pdf-helper/airyview-pdf-helper.exe').resolve()
def run(operation,src=source,**kw):
 q=dict(operation=operation,source=str(src),**kw);p=subprocess.run([str(helper)],input=json.dumps(q).encode(),stdout=subprocess.PIPE,stderr=subprocess.PIPE)
 result=json.loads(p.stdout);assert result['ok'],result;return result['result']
meta=run('inspect');assert len(meta['fields'])==2 and len(meta['bookmarks'])==1
out=root/'filled.pdf';run('write',destination=str(out),values={'name':'日本語入力テスト','agree':'/Yes'})
r=PdfReader(out);assert r.get_fields()['name']['/V']=='日本語入力テスト';assert r.get_fields()['agree']['/V']=='/Yes';assert r.pages[0]['/Annots'][0].get_object().get('/AP');print('PASS: standard form, Japanese value, checkbox and appearance retained')
added=root/'added.pdf';run('write',destination=str(added),text='日本語の記入',note='注釈テスト',bookmark='追加したしおり',page=1,x=30,y=30,size=12)
r=PdfReader(added);assert len(r.outline)==2;assert r.pages[1]['/Annots'][0].get_object()['/Contents']=='注釈テスト';assert '日本語の記入' in r.pages[1].extract_text();print('PASS: Japanese page text, native note and bookmark')
encrypted=root/'encrypted.pdf';w=PdfWriter();w.clone_document_from_reader(PdfReader(source));w.encrypt('secret',algorithm='AES-256');w.write(encrypted)
assert len(run('inspect',src=encrypted,password='secret')['fields'])==2
encout=root/'encrypted-filled.pdf';run('write',src=encrypted,password='secret',destination=str(encout),values={'name':'暗号化入力'})
r=PdfReader(encout);assert r.is_encrypted and r.decrypt('secret');assert r.get_fields()['name']['/V']=='暗号化入力';print('PASS: password-protected PDF and encrypted edited copy')
key=rsa.generate_private_key(public_exponent=65537,key_size=2048);subject=x509.Name([x509.NameAttribute(NameOID.COMMON_NAME,'AiryView TEST ONLY')]);now=datetime.datetime.now(datetime.timezone.utc)
cert=x509.CertificateBuilder().subject_name(subject).issuer_name(subject).public_key(key.public_key()).serial_number(x509.random_serial_number()).not_valid_before(now-datetime.timedelta(days=1)).not_valid_after(now+datetime.timedelta(days=30)).add_extension(x509.KeyUsage(True,True,False,False,False,False,False,False,False),critical=True).sign(key,hashes.SHA256())
pfx=root/'test-only.pfx';pfx.write_bytes(pkcs12.serialize_key_and_certificates(b'test',key,cert,None,serialization.BestAvailableEncryption(b'test')))
signed=root/'signed.pdf';run('sign',destination=str(signed),pfx=str(pfx),pfxPassword='test');status=run('verify',src=signed)['signatures'][0];assert status['valid'] and status['intact'] and not status['trusted'];print('PASS: certificate signature, integrity and untrusted self-signed status distinguished')
# Change a byte in a signed stream without changing its length.
data=signed.read_bytes();i=data.find(b'/MediaBox [ 0 0 595');assert i>=0;i+=len(b'/MediaBox [ 0 0 59');tampered=root/'tampered.pdf';tampered.write_bytes(data[:i]+b'6'+data[i+1:]);status=run('verify',src=tampered)['signatures'][0];assert not status.get('intact',True);print('PASS: tampering detected')
q=dict(operation='write',source=str(signed),destination=str(root/'must-not-exist.pdf'),text='blocked');p=subprocess.run([str(helper)],input=json.dumps(q).encode(),stdout=subprocess.PIPE);assert not json.loads(p.stdout)['ok'] and not (root/'must-not-exist.pdf').exists();print('PASS: signed source edit prohibited')

comb=root/'comb.pdf';c=canvas.Canvas(str(comb),pagesize=(595,842));c.acroForm.textfield(name='cells',x=50,y=700,width=80,height=25,fieldFlags='comb',maxlen=4);c.showPage();c.save()
comb_out=root/'comb-filled.pdf';run('write',src=comb,destination=str(comb_out),values={'cells':'1234'})
r=PdfReader(comb_out);assert r.get_fields()['cells']['/V']=='1234';assert int(r.get_fields()['cells']['/Ff']) & 16777216
(root/'must-not-overflow.pdf').unlink(missing_ok=True)
q=dict(operation='write',source=str(comb),destination=str(root/'must-not-overflow.pdf'),values={'cells':'12345'})
p=subprocess.run([str(helper)],input=json.dumps(q).encode(),stdout=subprocess.PIPE);assert not json.loads(p.stdout)['ok'];assert not (root/'must-not-overflow.pdf').exists();print('PASS: character-cell fields and maximum length retained')
