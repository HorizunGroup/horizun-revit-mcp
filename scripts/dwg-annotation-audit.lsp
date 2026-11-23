;; -----------------------------------------------------------------------------
;; Horizun Revit MCP - DWG annotation audit for AutoCAD / AutoCAD Core Console.
;;
;; Revit writes each view's native dimensions as DIMENSION entities in the model
;; space of that view's companion DWG (dwg_xrefs=linked) or inside the block that
;; represents the view (bound). The sheet file only references them, so a
;; top-level (ssget "_X" '((0 . "DIMENSION"))) over the sheet counts zero. This
;; script counts entities per block definition (xrefs included once resolved)
;; and per top-level space.
;;
;; Usage (Core Console):  accoreconsole.exe /i <file.dwg> /s audit.scr
;;   audit.scr:  (load "dwg-annotation-audit.lsp") (hz-audit "C:/out/file.txt") _.QUIT _Y
;; -----------------------------------------------------------------------------
(defun hz-bump (key tbl)
  (if (assoc key tbl)
    (subst (cons key (1+ (cdr (assoc key tbl)))) (assoc key tbl) tbl)
    (cons (cons key 1) tbl)))

(defun hz-count-chain (ename / tbl)
  (while ename
    (setq tbl (hz-bump (cdr (assoc 0 (entget ename))) tbl))
    (setq ename (entnext ename)))
  tbl)

(defun hz-audit (path / out b nm tbl ent e dims)
  (vl-load-com)
  (setq out (open path "w") dims 0)
  (setq b (tblnext "BLOCK" T))
  (while b
    (setq nm (cdr (assoc 2 b)))
    (setq tbl (hz-count-chain (entnext (tblobjname "BLOCK" nm))))
    (if (assoc "DIMENSION" tbl) (setq dims (+ dims (cdr (assoc "DIMENSION" tbl)))))
    (write-line (strcat "BLOCK " nm " flags=" (itoa (cdr (assoc 70 b))) " " (vl-prin1-to-string tbl)) out)
    (setq b (tblnext "BLOCK")))
  (setq tbl nil ent (entnext))
  (while ent
    (setq e (entget ent))
    (setq tbl (hz-bump (strcat (cdr (assoc 0 e)) "@" (if (assoc 410 e) (cdr (assoc 410 e)) "?")) tbl))
    (setq ent (entnext ent)))
  (write-line (strcat "TOP " (vl-prin1-to-string tbl)) out)
  (write-line (strcat "DIMENSION_IN_BLOCKS " (itoa dims)) out)
  (close out)
  (princ))
