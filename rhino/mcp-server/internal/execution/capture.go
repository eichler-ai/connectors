package execution

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"time"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
)

// CaptureOptions mirror the plug-in's CaptureRequest (PRD §11).
type CaptureOptions struct {
	DocumentID            string
	Target                string
	DisplayMode           string
	Zoom                  string
	Width, Height         int
	TransparentBackground bool
	DrawGrid, DrawAxes    *bool
}

// CapturedImage is one image as the plug-in returned it, decoded from base64.
type CapturedImage struct {
	Viewport string
	Width    int
	Height   int
	MIMEType string
	PNG      []byte
}

// CaptureResult is capture_view's decoded wire result.
type CaptureResult struct {
	Images  []CapturedImage
	Notices []diag.Record
}

type captureWire struct {
	Images []struct {
		Viewport   string `json:"viewport"`
		Width      int    `json:"width"`
		Height     int    `json:"height"`
		MIMEType   string `json:"mime_type"`
		DataBase64 string `json:"data_base64"`
	} `json:"images"`
	Notices []diag.Record `json:"notices"`
}

// captureTimeout bounds the wire call: the plug-in's main-thread wait is 30 s, plus rendering.
const captureTimeout = 45 * time.Second

// CaptureView forwards capture_view to the instance (PRD §11).
func (r *Router) CaptureView(ctx context.Context, instanceID string, opts CaptureOptions) (*CaptureResult, *diag.Record) {
	conn, ok := r.conns.Conn(instanceID)
	if !ok {
		return nil, diag.New(diag.SeverityError, "instance-not-found", source,
			fmt.Sprintf("no connected Rhino instance has instance_id %q", instanceID)).
			WithRemedy("call list_instances and pick a current instance_id")
	}
	params := map[string]any{
		"document_id": opts.DocumentID, "target": opts.Target, "zoom": opts.Zoom,
		"width": opts.Width, "height": opts.Height, "transparent_background": opts.TransparentBackground,
	}
	if opts.DisplayMode != "" {
		params["display_mode"] = opts.DisplayMode
	}
	if opts.DrawGrid != nil {
		params["draw_grid"] = *opts.DrawGrid
	}
	if opts.DrawAxes != nil {
		params["draw_axes"] = *opts.DrawAxes
	}
	wctx, cancel := context.WithTimeout(ctx, captureTimeout)
	defer cancel()
	raw, rpcErr, err := conn.Call(wctx, "capture_view", params)
	if err != nil {
		return nil, diag.New(diag.SeverityError, "wire-call-failed", source, fmt.Sprintf("capture_view did not complete: %v", err)).
			WithRemedy("if a script is running or Rhino is inside a modal, wait and retry")
	}
	if rpcErr != nil {
		if rpcErr.Data != nil {
			return nil, rpcErr.Data
		}
		return nil, diag.New(diag.SeverityError, "bridge-error", source, "capture_view was refused: "+rpcErr.Message)
	}
	// A `busy` answer shares the execution result shape.
	var status struct {
		Status      string `json:"status"`
		ExecutionID string `json:"execution_id"`
	}
	if json.Unmarshal(raw, &status) == nil && status.Status == "busy" {
		return nil, diag.New(diag.SeverityError, "instance-busy", source,
			fmt.Sprintf("a script is running on this instance (execution %s); capture_view waits for it", status.ExecutionID)).
			WithDetail(map[string]any{"execution_id": status.ExecutionID}).
			WithRemedy("poll_execution the running id until it is terminal, then capture again")
	}
	var w captureWire
	if err := json.Unmarshal(raw, &w); err != nil {
		return nil, diag.New(diag.SeverityError, "wire-decode-failed", source, "capture_view returned a result this server could not decode: "+err.Error())
	}
	res := &CaptureResult{Notices: w.Notices}
	for _, img := range w.Images {
		b, err := base64.StdEncoding.DecodeString(img.DataBase64)
		if err != nil {
			return nil, diag.New(diag.SeverityError, "wire-decode-failed", source, "capture_view image for "+img.Viewport+" is not valid base64")
		}
		mime := img.MIMEType
		if mime == "" {
			mime = "image/png"
		}
		res.Images = append(res.Images, CapturedImage{Viewport: img.Viewport, Width: img.Width, Height: img.Height, MIMEType: mime, PNG: b})
	}
	return res, nil
}
