package store

import (
	"context"
	"errors"
	"fmt"
	"time"

	"cloud.google.com/go/firestore"
	"google.golang.org/api/iterator"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
)

// Firestore is the deployed Store (PRD §11). One document per record, the
// record's hash or id as the document id, and the atomic steps as
// transactions. Expiry is a Firestore TTL policy on `expires_at` for
// auth_codes, login_states, refresh_tokens and bridge_tokens (deploy.sh
// sets it); reads still check the field themselves because TTL deletion
// lags by up to a day. No ORM, no indexes beyond the automatic single-field
// ones: the two multi-field lookups (family revocation, unused-client
// collection) filter the second field in code.
type Firestore struct {
	c *firestore.Client
}

// Collection names, fixed by PRD §11 (plus identities and login_states).
const (
	colUsers      = "users"
	colIdentities = "identities"
	colClients    = "oauth_clients"
	colStates     = "login_states"
	colCodes      = "auth_codes"
	colRefresh    = "refresh_tokens"
	colBridge     = "bridge_tokens"
	colGraph      = "graph_tokens"
	colAudit      = "audit"
	subAuditRows  = "rows"
)

// NewFirestore connects to database in project ("(default)" when database
// is empty). Credentials come from the environment (the Cloud Run runtime
// account, or gcloud's application-default credentials locally).
func NewFirestore(ctx context.Context, project, database string) (*Firestore, error) {
	if database == "" {
		database = firestore.DefaultDatabaseID
	}
	c, err := firestore.NewClientWithDatabase(ctx, project, database)
	if err != nil {
		return nil, fmt.Errorf("firestore: %w", err)
	}
	return &Firestore{c: c}, nil
}

func notFound(err error) bool { return status.Code(err) == codes.NotFound }

func (f *Firestore) UserByIdentity(ctx context.Context, provider, subject string) (User, error) {
	ds, err := f.c.Collection(colIdentities).Doc(identityKey(provider, subject)).Get(ctx)
	if err != nil {
		if notFound(err) {
			return User{}, ErrNotFound
		}
		return User{}, err
	}
	var link struct {
		UserID string `firestore:"user_id"`
	}
	if err := ds.DataTo(&link); err != nil {
		return User{}, err
	}
	return f.User(ctx, link.UserID)
}

func (f *Firestore) User(ctx context.Context, id string) (User, error) {
	us, err := f.c.Collection(colUsers).Doc(id).Get(ctx)
	if err != nil {
		if notFound(err) {
			return User{}, ErrNotFound
		}
		return User{}, err
	}
	var u User
	return u, us.DataTo(&u)
}

func (f *Firestore) PutUser(ctx context.Context, u User) error {
	// Two writes, user first: a crash between them leaves a user without
	// its identity link, and the next login simply creates a fresh user —
	// the reverse order could leave a link to a user that does not exist.
	if _, err := f.c.Collection(colUsers).Doc(u.ID).Set(ctx, u); err != nil {
		return err
	}
	_, err := f.c.Collection(colIdentities).Doc(identityKey(u.Provider, u.Subject)).Set(ctx, map[string]any{"user_id": u.ID})
	return err
}

func (f *Firestore) PutClient(ctx context.Context, c Client) error {
	_, err := f.c.Collection(colClients).Doc(c.ID).Set(ctx, c)
	return err
}

func (f *Firestore) Client(ctx context.Context, id string) (Client, error) {
	ds, err := f.c.Collection(colClients).Doc(id).Get(ctx)
	if err != nil {
		if notFound(err) {
			return Client{}, ErrNotFound
		}
		return Client{}, err
	}
	var c Client
	return c, ds.DataTo(&c)
}

func (f *Firestore) TouchClient(ctx context.Context, id string, at time.Time) error {
	_, err := f.c.Collection(colClients).Doc(id).Update(ctx, []firestore.Update{{Path: "last_used_at", Value: at}})
	if notFound(err) {
		return ErrNotFound
	}
	return err
}

func (f *Firestore) DeleteUnusedClients(ctx context.Context, cutoff time.Time) (int, error) {
	it := f.c.Collection(colClients).Where("created_at", "<", cutoff).Documents(ctx)
	defer it.Stop()
	n := 0
	for {
		ds, err := it.Next()
		if errors.Is(err, iterator.Done) {
			return n, nil
		}
		if err != nil {
			return n, err
		}
		var c Client
		if err := ds.DataTo(&c); err != nil {
			return n, err
		}
		if !c.LastUsedAt.IsZero() {
			continue
		}
		if _, err := ds.Ref.Delete(ctx); err != nil {
			return n, err
		}
		n++
	}
}

func (f *Firestore) PutLoginState(ctx context.Context, s LoginState) error {
	_, err := f.c.Collection(colStates).Doc(s.ID).Set(ctx, s)
	return err
}

func (f *Firestore) LoginState(ctx context.Context, id string) (LoginState, error) {
	ds, err := f.c.Collection(colStates).Doc(id).Get(ctx)
	if err != nil {
		if notFound(err) {
			return LoginState{}, ErrNotFound
		}
		return LoginState{}, err
	}
	var s LoginState
	if err := ds.DataTo(&s); err != nil {
		return LoginState{}, err
	}
	if s.ExpiresAt.Before(time.Now()) {
		return LoginState{}, ErrNotFound
	}
	return s, nil
}

func (f *Firestore) DeleteLoginState(ctx context.Context, id string) error {
	_, err := f.c.Collection(colStates).Doc(id).Delete(ctx)
	return err
}

func (f *Firestore) PutAuthCode(ctx context.Context, c AuthCode) error {
	_, err := f.c.Collection(colCodes).Doc(c.Hash).Set(ctx, c)
	return err
}

// Reuse detection inside a transaction: returning an error from the
// transaction body rolls its writes back, which would undo the family
// revocation. So the body commits the revocation and reports reuse through
// a flag, and the error is produced after the commit.
func (f *Firestore) ConsumeAuthCode(ctx context.Context, hash string, now time.Time) (AuthCode, error) {
	var out AuthCode
	reused := false
	err := f.c.RunTransaction(ctx, func(ctx context.Context, tx *firestore.Transaction) error {
		reused = false
		ref := f.c.Collection(colCodes).Doc(hash)
		ds, err := tx.Get(ref)
		if err != nil {
			if notFound(err) {
				return ErrNotFound
			}
			return err
		}
		var c AuthCode
		if err := ds.DataTo(&c); err != nil {
			return err
		}
		if c.ExpiresAt.Before(now) {
			return ErrNotFound
		}
		if !c.UsedAt.IsZero() {
			reused = true
			return f.revokeFamilyTx(ctx, tx, c.ID)
		}
		out = c
		out.UsedAt = now
		return tx.Update(ref, []firestore.Update{{Path: "used_at", Value: now}})
	})
	if err == nil && reused {
		return AuthCode{}, ErrReused
	}
	return out, err
}

func (f *Firestore) PutRefreshToken(ctx context.Context, t RefreshToken) error {
	_, err := f.c.Collection(colRefresh).Doc(t.Hash).Set(ctx, t)
	return err
}

func (f *Firestore) RotateRefreshToken(ctx context.Context, oldHash string, now time.Time, next RefreshToken) (RefreshToken, error) {
	var old RefreshToken
	reused := false
	err := f.c.RunTransaction(ctx, func(ctx context.Context, tx *firestore.Transaction) error {
		reused = false
		ref := f.c.Collection(colRefresh).Doc(oldHash)
		ds, err := tx.Get(ref)
		if err != nil {
			if notFound(err) {
				return ErrNotFound
			}
			return err
		}
		if err := ds.DataTo(&old); err != nil {
			return err
		}
		if old.ExpiresAt.Before(now) {
			return ErrNotFound
		}
		if !old.UsedAt.IsZero() {
			reused = true
			return f.revokeFamilyTx(ctx, tx, old.FamilyID)
		}
		if old.Revoked {
			return ErrRevoked
		}
		if err := tx.Update(ref, []firestore.Update{{Path: "used_at", Value: now}}); err != nil {
			return err
		}
		return tx.Set(f.c.Collection(colRefresh).Doc(next.Hash), inherit(next, old))
	})
	if err == nil && reused {
		return RefreshToken{}, ErrReused
	}
	return old, err
}

// revokeFamilyTx must run after every read the transaction performs, since
// Firestore forbids reads after writes; both callers read one document
// first and this is their last read.
func (f *Firestore) revokeFamilyTx(ctx context.Context, tx *firestore.Transaction, familyID string) error {
	docs, err := tx.Documents(f.c.Collection(colRefresh).Where("family_id", "==", familyID)).GetAll()
	if err != nil {
		return err
	}
	for _, d := range docs {
		if err := tx.Update(d.Ref, []firestore.Update{{Path: "revoked", Value: true}}); err != nil {
			return err
		}
	}
	return nil
}

func (f *Firestore) RevokeFamily(ctx context.Context, familyID string) error {
	return f.c.RunTransaction(ctx, func(ctx context.Context, tx *firestore.Transaction) error {
		return f.revokeFamilyTx(ctx, tx, familyID)
	})
}

func (f *Firestore) RevokeUser(ctx context.Context, userID string) (int, error) {
	docs, err := f.c.Collection(colRefresh).Where("user_id", "==", userID).Documents(ctx).GetAll()
	if err != nil {
		return 0, err
	}
	n := 0
	for _, d := range docs {
		var t RefreshToken
		if err := d.DataTo(&t); err != nil {
			return n, err
		}
		if t.Revoked {
			continue
		}
		if _, err := d.Ref.Update(ctx, []firestore.Update{{Path: "revoked", Value: true}}); err != nil {
			return n, err
		}
		n++
	}
	// Bridge tokens are deleted rather than flagged (see store.BridgeToken).
	docs, err = f.c.Collection(colBridge).Where("user_id", "==", userID).Documents(ctx).GetAll()
	if err != nil {
		return n, err
	}
	for _, d := range docs {
		if _, err := d.Ref.Delete(ctx); err != nil {
			return n, err
		}
		n++
	}
	// The Graph token, keyed by user id directly (one per user, unlike
	// refresh/bridge tokens which are keyed by hash).
	if _, err := f.c.Collection(colGraph).Doc(userID).Get(ctx); err == nil {
		if _, err := f.c.Collection(colGraph).Doc(userID).Delete(ctx); err != nil {
			return n, err
		}
		n++
	} else if !notFound(err) {
		return n, err
	}
	return n, nil
}

func (f *Firestore) PutGraphToken(ctx context.Context, t GraphToken) error {
	_, err := f.c.Collection(colGraph).Doc(t.UserID).Set(ctx, t)
	return err
}

func (f *Firestore) GraphToken(ctx context.Context, userID string) (GraphToken, error) {
	ds, err := f.c.Collection(colGraph).Doc(userID).Get(ctx)
	if err != nil {
		if notFound(err) {
			return GraphToken{}, ErrNotFound
		}
		return GraphToken{}, err
	}
	var t GraphToken
	return t, ds.DataTo(&t)
}

func (f *Firestore) DeleteGraphToken(ctx context.Context, userID string) error {
	_, err := f.c.Collection(colGraph).Doc(userID).Delete(ctx)
	return err
}

func (f *Firestore) PutBridgeToken(ctx context.Context, t BridgeToken) error {
	_, err := f.c.Collection(colBridge).Doc(t.Hash).Set(ctx, t)
	return err
}

func (f *Firestore) BridgeToken(ctx context.Context, hash string) (BridgeToken, error) {
	ds, err := f.c.Collection(colBridge).Doc(hash).Get(ctx)
	if err != nil {
		if notFound(err) {
			return BridgeToken{}, ErrNotFound
		}
		return BridgeToken{}, err
	}
	var t BridgeToken
	if err := ds.DataTo(&t); err != nil {
		return BridgeToken{}, err
	}
	if t.ExpiresAt.Before(time.Now()) {
		return BridgeToken{}, ErrNotFound
	}
	return t, nil
}

func (f *Firestore) TouchBridgeToken(ctx context.Context, hash string, at time.Time) error {
	_, err := f.c.Collection(colBridge).Doc(hash).Update(ctx, []firestore.Update{{Path: "last_used_at", Value: at}})
	if notFound(err) {
		return ErrNotFound
	}
	return err
}

func (f *Firestore) RevokeBridgeToken(ctx context.Context, hash string) error {
	// Delete of a missing document succeeds in Firestore, matching the
	// contract.
	_, err := f.c.Collection(colBridge).Doc(hash).Delete(ctx)
	return err
}

// auditRows is audit/{user_id}/rows: a subcollection per user (§11 "per
// user"), so RevokeUser-style scoped deletion is a single collection delete
// if that is ever added, and the TTL policy (deploy.sh) applies to every
// user's rows via the "rows" collection group.
func (f *Firestore) auditRows(userID string) *firestore.CollectionRef {
	return f.c.Collection(colAudit).Doc(userID).Collection(subAuditRows)
}

func (f *Firestore) PutAuditRow(ctx context.Context, row AuditRow) error {
	_, err := f.auditRows(row.UserID).Doc(row.ID).Set(ctx, row)
	return err
}

func (f *Firestore) RecentAudit(ctx context.Context, userID string, limit int) ([]AuditRow, error) {
	q := f.auditRows(userID).OrderBy("timestamp", firestore.Desc)
	if limit > 0 {
		q = q.Limit(limit)
	}
	it := q.Documents(ctx)
	defer it.Stop()
	var out []AuditRow
	for {
		ds, err := it.Next()
		if errors.Is(err, iterator.Done) {
			return out, nil
		}
		if err != nil {
			return out, err
		}
		var row AuditRow
		if err := ds.DataTo(&row); err != nil {
			return out, err
		}
		out = append(out, row)
	}
}

func (f *Firestore) Close() error { return f.c.Close() }
