"""Guard any future execution with the packed-input audit discovered during this check."""
import subprocess,sys

if __name__=='__main__':
    audit=subprocess.run([sys.executable,'scripts/causal/repair_audit.py','--gate'])
    if audit.returncode:
        raise SystemExit('Training blocked: the historical corpus loses required context or has contradictory tool targets. Inspect packed-input-audit.json; do not bypass this gate.')
    from repair_train import main
    main()
