package files

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"time"

	"cloud.google.com/go/compute/metadata"
	credentials "cloud.google.com/go/iam/credentials/apiv1"
	"cloud.google.com/go/iam/credentials/apiv1/credentialspb"
	"cloud.google.com/go/storage"
)

// GCS is the production Store (PRD §11): one bucket, objects at
// files/{user_id}/{connector}/{id}.{ext}, uniform bucket-level access, a
// 7-day lifecycle-delete rule (deploy.sh creates the bucket with these
// settings — there is nothing here that depends on them beyond the object
// path).
//
// The Cloud Run runtime service account has no private key to sign a URL
// with locally, so SignedURL asks the IAM Credentials API to sign on its
// behalf (the "SignBytes via signBlob" path the storage client supports)
// rather than the PrivateKey path, which needs a key file this deployment
// deliberately has none of. That is why deploy.sh grants the runtime
// service account roles/iam.serviceAccountTokenCreator on itself — signBlob
// is a self-impersonation call.
type GCS struct {
	bucket string
	sa     string
	client *storage.Client
	iam    *credentials.IamCredentialsClient
}

// NewGCS opens a client for bucket and discovers the runtime service account
// email from the metadata server (only present on Cloud Run/GCE — this Store
// is never constructed in -dev, see cmd/hub).
func NewGCS(ctx context.Context, bucket string) (*GCS, error) {
	sa, err := metadata.EmailWithContext(ctx, "default")
	if err != nil {
		return nil, fmt.Errorf("files: runtime service account from metadata server: %w", err)
	}
	client, err := storage.NewClient(ctx)
	if err != nil {
		return nil, fmt.Errorf("files: storage client: %w", err)
	}
	iamClient, err := credentials.NewIamCredentialsClient(ctx)
	if err != nil {
		return nil, fmt.Errorf("files: iam credentials client: %w", err)
	}
	return &GCS{bucket: bucket, sa: sa, client: client, iam: iamClient}, nil
}

func (g *GCS) Put(ctx context.Context, userID, connector, id, ext string, r io.Reader) (ObjectRef, error) {
	key := fmt.Sprintf("files/%s/%s/%s.%s", userID, connector, id, ext)
	w := g.client.Bucket(g.bucket).Object(key).NewWriter(ctx)
	n, copyErr := io.Copy(w, r)
	closeErr := w.Close()
	if copyErr != nil {
		return ObjectRef{}, fmt.Errorf("files: write %s: %w", key, copyErr)
	}
	if closeErr != nil {
		return ObjectRef{}, fmt.Errorf("files: finalize %s: %w", key, closeErr)
	}
	return ObjectRef{Key: key, Bytes: n}, nil
}

func (g *GCS) SignedURL(ctx context.Context, ref ObjectRef, ttl time.Duration) (string, time.Time, error) {
	expires := time.Now().Add(ttl)
	opts := &storage.SignedURLOptions{
		GoogleAccessID: g.sa,
		Scheme:         storage.SigningSchemeV4,
		Method:         http.MethodGet,
		Expires:        expires,
		SignBytes: func(b []byte) ([]byte, error) {
			resp, err := g.iam.SignBlob(ctx, &credentialspb.SignBlobRequest{
				Name:    "projects/-/serviceAccounts/" + g.sa,
				Payload: b,
			})
			if err != nil {
				return nil, fmt.Errorf("files: signBlob: %w", err)
			}
			return resp.SignedBlob, nil
		},
	}
	u, err := storage.SignedURL(g.bucket, ref.Key, opts)
	if err != nil {
		return "", time.Time{}, fmt.Errorf("files: sign %s: %w", ref.Key, err)
	}
	return u, expires, nil
}
