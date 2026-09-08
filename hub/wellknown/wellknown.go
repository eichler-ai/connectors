// Package wellknown holds the static documents the hub serves under
// /.well-known/ that are not generated at runtime.
//
// microsoft-identity-association.json is how Microsoft Entra verifies that
// eichler.ai is the publisher domain of the "Eichler Connectors" app
// registration (the consent screen shows "unverified" otherwise). Entra
// fetches it from https://eichler.ai/.well-known/microsoft-identity-association.json,
// so the apex domain is mapped to the hub service and the file lists the
// registration's Application (client) ID.
package wellknown

import _ "embed"

//go:embed microsoft-identity-association.json
var MicrosoftIdentityAssociation []byte
