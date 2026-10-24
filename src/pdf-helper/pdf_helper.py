import io, json, os, sys, tempfile, ssl
from pathlib import Path
from pypdf import PdfReader, PdfWriter
from pypdf.generic import NameObject, TextStringObject, NumberObject, BooleanObject, DictionaryObject, ArrayObject, DecodedStreamObject
from pypdf.annotations import Text


def reader_for(q):
    r = PdfReader(q['source'])
    if r.is_encrypted and not r.decrypt(q.get('password', '')):
        raise ValueError('PDFのパスワードが正しくありません。')
    if len(r.pages) > 10000:
        raise ValueError('PDFは10,000ページ以内にしてください。')
    return r


def field_map(r):
    result=r.get_fields() or {}
    form=r.trailer['/Root'].get('/AcroForm')
    if not form:return result
    def walk(ref,prefix='',inherited=None):
        node=ref.get_object();props=dict(inherited or {})
        for key in ('/MaxLen','/Ff','/FT','/Opt'):
            if key in node:props[key]=node[key]
        name=prefix
        if '/T' in node:name=(prefix+'.' if prefix else '')+str(node['/T'])
        if name in result:
            for key,value in props.items():result[name][NameObject(key)]=value
        for kid in node.get('/Kids',[]):walk(kid,name,props)
    for ref in form.get_object().get('/Fields',[]):walk(ref)
    return result

def inspect_pdf(q):
    r = reader_for(q)
    fields = field_map(r)
    rows=[]
    for name, f in fields.items():
        kind=str(f.get('/FT',''))
        if kind in ('/Tx','/Btn','/Ch'):
            rows.append(dict(name=name, kind=kind, value=str(f.get('/V','')), flags=int(f.get('/Ff',0)), maxLen=int(f.get('/MaxLen',0) or 0), options=[str(x[0]) if isinstance(x,list) else str(x) for x in f.get('/Opt',[])], on='/Yes'))
    for page in r.pages:
        for ref in page.get('/Annots',[]):
            widget=ref.get_object();field=widget
            while '/T' not in field and '/Parent' in field:field=field['/Parent'].get_object()
            name=str(field.get('/T',''));parent=field
            while '/Parent' in parent:
                parent=parent['/Parent'].get_object()
                if '/T' in parent:name=str(parent['/T'])+'.'+name
            if '/AP' in widget and isinstance(widget['/AP'].get('/N'),DictionaryObject):
                export=next((str(k) for k in widget['/AP']['/N'] if str(k)!='/Off'),'/Yes')
                for row in rows:
                    if row['name']==name:row['on']=export
    outlines=[]
    def walk(items, depth=0):
        for item in items:
            if isinstance(item,list): walk(item,depth+1)
            else:
                try:
                    destination=r.get_destination_page_number(item)
                    if isinstance(destination,int) and destination>=0:outlines.append(dict(title=item.title,page=destination,depth=depth))
                except Exception: pass
    walk(r.outline)
    notes=[]
    for i,page in enumerate(r.pages):
        for ref in page.get('/Annots',[]):
            a=ref.get_object()
            if '/Contents' in a and str(a.get('/Subtype'))!='/Widget':
                notes.append(dict(page=i,text=str(a['/Contents']),kind=str(a.get('/Subtype',''))))
    return dict(fields=rows, bookmarks=outlines, notes=notes, xfa=bool(r.trailer['/Root'].get('/AcroForm',{}).get_object().get('/XFA')) if '/AcroForm' in r.trailer['/Root'] else False)


def japanese_page(text,w,h,size=12,comb=0):
    from reportlab.pdfgen import canvas
    from reportlab.pdfbase import pdfmetrics
    from reportlab.pdfbase.ttfonts import TTFont
    if 'AiryJP' not in pdfmetrics.getRegisteredFontNames():
        pdfmetrics.registerFont(TTFont('AiryJP',os.path.join(os.environ['WINDIR'],'Fonts','msgothic.ttc'),subfontIndex=0))
    b=io.BytesIO(); c=canvas.Canvas(b,pagesize=(w,h));c.setFont('AiryJP',size)
    y=h-size-2
    for line in text.splitlines() or ['']:
        if comb:
            for i,ch in enumerate(line[:comb]):c.drawCentredString((i+.5)*w/comb,y,ch)
        else:c.drawString(2,y,line)
        y-=size*1.3
    c.save();b.seek(0);return PdfReader(b).pages[0]


def set_appearance(w, widget, text, field):
    box=widget['/Rect'];width=float(box[2])-float(box[0]);height=float(box[3])-float(box[1])
    page=japanese_page(text,width,height,min(12,max(5,height-4)),int(field.get("/MaxLen",0)) if int(field.get("/Ff",0)) & 16777216 else 0)
    stream=DecodedStreamObject();stream.set_data(page.get_contents().get_data())
    stream.update({NameObject('/Type'):NameObject('/XObject'),NameObject('/Subtype'):NameObject('/Form'),NameObject('/BBox'):ArrayObject([NumberObject(0),NumberObject(0),NumberObject(width),NumberObject(height)]),NameObject('/Resources'):page['/Resources'].clone(w)})
    widget[NameObject('/AP')]=DictionaryObject({NameObject('/N'):w._add_object(stream)})


def write_pdf(q):
    r=reader_for(q)
    fields=field_map(r)
    if any(str(f.get('/FT'))=='/Sig' and f.get('/V') for f in fields.values()):
        raise ValueError('署名済みPDFは書き換えず、署名のない原本を編集してください。')
    if r.is_encrypted and r.user_access_permissions is not None:
        permissions=int(r.user_access_permissions)
        form_only=bool(q.get('values')) and not any(q.get(k) for k in ('text','note','bookmark'))
        if not permissions & 8 and not (form_only and permissions & 256):raise ValueError('このPDFは編集が制限されています。')
    w=PdfWriter();w.clone_document_from_reader(r)
    values=q.get('values',{})
    for name in values:
        if name not in fields or int(fields[name].get('/Ff',0)) & 1: raise ValueError('入力できないフィールドです: '+name)
        maximum=int(fields[name].get('/MaxLen',0) or 0)
        if maximum and len(values[name])>maximum:raise ValueError('入力欄の文字数制限を超えています。')
    if values:
        standard={k:v for k,v in values.items() if str(fields[k].get('/FT'))!='/Tx'}
        if standard: w.update_page_form_field_values(None,standard,auto_regenerate=False)
        for page in w.pages:
            for ref in page.get('/Annots',[]):
                widget=ref.get_object();field=widget
                while '/T' not in field and '/Parent' in field: field=field['/Parent'].get_object()
                name=str(field.get('/T',''))
                # Qualified hierarchical field names.
                parent=field
                while '/Parent' in parent:
                    parent=parent['/Parent'].get_object()
                    if '/T' in parent:name=str(parent['/T'])+'.'+name
                if name in values and str(field.get('/FT',fields[name].get('/FT'))) == '/Tx':
                    field[NameObject('/V')]=TextStringObject(values[name]);set_appearance(w,widget,values[name],fields[name])
        if '/AcroForm' in w._root_object:w._root_object['/AcroForm'][NameObject('/NeedAppearances')]=BooleanObject(False)
    if '/AcroForm' in w._root_object and '/XFA' in w._root_object['/AcroForm']:raise ValueError('XFAフォームは編集できません。')
    page_index=int(q.get('page',0))
    if not 0<=page_index<len(w.pages):raise ValueError('ページ番号が不正です。')
    if q.get('text'):
        page=w.pages[page_index];size=float(q.get('size',12));x=float(q.get('x',30))*72/25.4;y=float(q.get('y',30))*72/25.4
        overlay=japanese_page(q['text'],float(page.mediabox.width),float(page.mediabox.height),size)
        from pypdf import Transformation
        # japanese_page draws at (2,H-size-2); move to user's top-left position.
        page.merge_transformed_page(overlay,Transformation().translate(x-2,-y+2),expand=False)
    if q.get('note'):
        page=w.pages[page_index];top=float(page.mediabox.height)
        x=float(q.get('x',30))*72/25.4;y=top-float(q.get('y',30))*72/25.4
        annotation=Text(rect=(x,y-24,x+24,y),text=q['note'],flags=4)
        w.add_annotation(page_index,annotation)
    if q.get('bookmark'):w.add_outline_item(q['bookmark'],page_index)
    if r.is_encrypted:
        # Retain encryption. The recipient opens the edited copy with the supplied password.
        w.encrypt(q.get('password',''),algorithm='AES-256',permissions_flag=r.user_access_permissions)
    with open(q['temporary'],'wb') as f:w.write(f)
    check=PdfReader(q['temporary'])
    if check.is_encrypted:check.decrypt(q.get('password',''))
    for name,value in values.items():
        if str((check.get_fields() or {})[name].get('/V','')) != value:raise ValueError('保存値の検証に失敗: '+name)
    return {'saved':True}


def sign_pdf(q):
    from pyhanko.sign import signers, fields
    from pyhanko.pdf_utils.incremental_writer import IncrementalPdfFileWriter
    signer=signers.SimpleSigner.load_pkcs12(q['pfx'],passphrase=q.get('pfxPassword','').encode('utf-8'))
    if signer is None:raise ValueError('証明書またはパスワードを確認してください。')
    with open(q['source'],'rb') as f:
        writer=IncrementalPdfFileWriter(f)
        if writer.prev.encrypted:writer.encrypt(q.get('password',''))
        name='AirySignature_'+os.urandom(6).hex()
        metadata=signers.PdfSignatureMetadata(field_name=name,md_algorithm='sha256',subfilter=fields.SigSeedSubFilter.PADES)
        with open(q['temporary'],'wb') as out:
            signers.PdfSigner(metadata,signer=signer,new_field_spec=fields.SigFieldSpec(sig_field_name=name)).sign_pdf(writer,output=out)
    return {'saved':True}


def verify_pdf(q):
    from asn1crypto import x509
    from pyhanko.pdf_utils.reader import PdfFileReader
    from pyhanko.sign.validation import validate_pdf_signature
    from pyhanko_certvalidator import ValidationContext
    roots=[]
    for cert,encoding,trust in ssl.enum_certificates('ROOT'):
        if encoding=='x509_asn':roots.append(x509.Certificate.load(cert))
    results=[]
    with open(q['source'],'rb') as f:
        r=PdfFileReader(f)
        if r.encrypted:r.decrypt(q.get('password',''))
        for sig in r.embedded_signatures:
            try:
                status=validate_pdf_signature(sig,ValidationContext(trust_roots=roots,allow_fetching=False,revocation_mode='soft-fail'))
                results.append(dict(name=sig.field_name,intact=status.intact,valid=status.valid,trusted=status.trusted,coverage=str(status.coverage),modifications_ok=status.docmdp_ok,subject=status.signing_cert.subject.human_friendly,details=status.pretty_print_details(),revocation='オンライン失効確認は未実施。オフライン検証結果です。'))
            except Exception as e:results.append(dict(name=sig.field_name,error=str(e)))
    return dict(signatures=results)


def main():
    q=json.load(sys.stdin)
    if not 0 < os.path.getsize(q['source']) <= 128 * 1024 * 1024:
        raise ValueError('PDFは128MB以内にしてください。')
    if q['operation'] in ('write','sign'):
        source=os.path.normcase(os.path.realpath(q['source']));dest=os.path.normcase(os.path.realpath(q['destination']))
        if source==dest:raise ValueError('元のPDFとは別の保存先を指定してください。')
        fd,tmp=tempfile.mkstemp(prefix='.airy-',suffix='.pdf',dir=os.path.dirname(q['destination']));os.close(fd);q['temporary']=tmp
        try:
            result=write_pdf(q) if q['operation']=='write' else sign_pdf(q)
            os.replace(tmp,q['destination'])
        finally:
            if os.path.exists(tmp):os.unlink(tmp)
    elif q['operation']=='inspect':result=inspect_pdf(q)
    elif q['operation']=='verify':result=verify_pdf(q)
    else:raise ValueError('不明な操作です。')
    print(json.dumps(dict(ok=True,result=result),ensure_ascii=True))
if __name__=='__main__':
    try:main()
    except Exception as e:
        print(json.dumps(dict(ok=False,error=str(e)),ensure_ascii=True));sys.exit(1)
