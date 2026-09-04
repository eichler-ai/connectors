package selfcheck

// PrimaryFailures counts consecutive failed attempts by a secondary to reach
// the SAME primary (identified by the pid broker.json names), so the
// re-election loop can stop retrying silently and report an unresponsive
// primary instead (issue #201).
//
// The live case: a primary that had died with its listener and lock still
// held. Every secondary dialed it, sent auth, hit the 10 s read timeout,
// re-raced the lock, lost to the corpse, and looped -- the MCP client saw an
// opaque CONNECT_TIMEOUT for as long as anyone cared to wait. Three strikes
// against one pid is ~30 s, long enough to ride out a primary that is merely
// still starting, short enough that the client's failure is legible.
type PrimaryFailures struct {
	// Threshold is the number of consecutive failures against one pid that
	// counts as unresponsive. Zero means the default of 3.
	Threshold int

	pid   int
	count int
}

// Record notes one failed attempt against the primary with the given pid and
// reports whether the threshold has been reached. A different pid resets the
// count: a new primary deserves a fresh chance. Call Reset after a success.
func (f *PrimaryFailures) Record(pid int) (giveUp bool) {
	if pid != f.pid {
		f.pid = pid
		f.count = 0
	}
	f.count++
	return f.count >= f.threshold()
}

// Reset forgets prior failures (after a successful proxy session).
func (f *PrimaryFailures) Reset() {
	f.pid = 0
	f.count = 0
}

// Count is the current consecutive-failure count (for the log line).
func (f *PrimaryFailures) Count() int { return f.count }

func (f *PrimaryFailures) threshold() int {
	if f.Threshold <= 0 {
		return 3
	}
	return f.Threshold
}
