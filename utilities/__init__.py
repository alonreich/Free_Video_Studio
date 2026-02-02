import sys
import os

# Enforce no-cache policy for the utilities package
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
