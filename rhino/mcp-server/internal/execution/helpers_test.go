package execution

import "github.com/eichler-ai/connectors/internal/servercore/diag"

func diagRecord(code string) *diag.Record { return diag.New(diag.SeverityError, code, "test", code) }
