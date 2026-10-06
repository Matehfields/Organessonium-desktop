'use strict';
// Independent reference using Node/OpenSSL, not the C# codec. Test keys arrive on stdin only.
const crypto = require('node:crypto');
function key(master, salt) {
    return Buffer.from(crypto.hkdfSync('sha256', master, salt, Buffer.from('organessonium/metadata-key/v1', 'utf8'), 32));
}
function check(derived) {
    return crypto.createHmac('sha256', derived).update('organessonium/metadata-key-check/v1', 'utf8').digest().subarray(0, 16).toString('base64');
}
function aad(e) {
    return Buffer.from(['organessonium-metadata', '1', 'true', 'AES-256-GCM', 'HKDF-SHA256', e.keyMode, e.salt, e.nonce, e.keyCheck].join('\n'), 'utf8');
}
function encrypt(request) {
    const salt = request.saltHex ? Buffer.from(request.saltHex, 'hex') : crypto.randomBytes(16);
    const nonce = request.nonceHex ? Buffer.from(request.nonceHex, 'hex') : crypto.randomBytes(12);
    const derived = key(Buffer.from(request.masterHex, 'hex'), salt);
    const e = {format:'organessonium-metadata',version:1,encrypted:true,algorithm:'AES-256-GCM',kdf:'HKDF-SHA256',keyMode:request.keyMode || 'standard',salt:salt.toString('base64'),nonce:nonce.toString('base64'),keyCheck:check(derived)};
    const aes = crypto.createCipheriv('aes-256-gcm', derived, nonce, {authTagLength:16});
    aes.setAAD(aad(e));
    e.ciphertext = Buffer.concat([aes.update(Buffer.from(request.plaintext, 'utf8')), aes.final()]).toString('base64');
    e.tag = aes.getAuthTag().toString('base64');
    derived.fill(0);
    return e;
}
function decrypt(request) {
    const e = request.envelope;
    if (e.format !== 'organessonium-metadata' || e.version !== 1 || e.encrypted !== true || e.algorithm !== 'AES-256-GCM' || e.kdf !== 'HKDF-SHA256') throw Error('Unsupported envelope');
    const derived = key(Buffer.from(request.masterHex,'hex'), Buffer.from(e.salt,'base64'));
    if (check(derived) !== e.keyCheck) throw Error('Key mismatch');
    const aes = crypto.createDecipheriv('aes-256-gcm', derived, Buffer.from(e.nonce,'base64'), {authTagLength:16});
    aes.setAAD(aad(e)); aes.setAuthTag(Buffer.from(e.tag,'base64'));
    const plaintext = Buffer.concat([aes.update(Buffer.from(e.ciphertext,'base64')), aes.final()]).toString('utf8');
    derived.fill(0);
    JSON.parse(plaintext);
    return plaintext;
}
let input = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', chunk => input += chunk);
process.stdin.on('end', () => {
    try { const request = JSON.parse(input); process.stdout.write(JSON.stringify(request.operation === 'decrypt' ? decrypt(request) : encrypt(request))); }
    catch { process.stderr.write('Reference metadata codec failed'); process.exitCode = 1; }
});
