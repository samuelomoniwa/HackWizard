<# Creates an ECDSA P-256 key pair for signing a published knowledge base.
   Put the printed public key into KnowledgeBaseLoader.ManifestPublicKey; keep the private key OUT of the repository. #>
param([string]$PrivateKeyPath = "kb-signing-key.pkcs8")
$ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
[IO.File]::WriteAllBytes($PrivateKeyPath, $ecdsa.ExportPkcs8PrivateKey())
Write-Host "Private key written to $PrivateKeyPath"
[Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())
