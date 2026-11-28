"""One local driver's request to the isolated exploration field test."""
import argparse
import json
from pathlib import Path
import time
import uuid


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('seat',type=Path)
    parser.add_argument('tool')
    parser.add_argument('arguments',nargs='?',default='{}')
    args=parser.parse_args()
    deadline=time.monotonic()+240
    while not (args.seat/'ready').exists():
        if time.monotonic()>=deadline:
            raise TimeoutError('field not ready')
        time.sleep(.25)
    key=uuid.uuid4().hex
    tmp=args.seat/(key+'.tmp')
    tmp.write_text(json.dumps(dict(tool=args.tool,args=json.loads(args.arguments))),encoding='utf-8')
    tmp.replace(args.seat/(key+'.request'))
    response=args.seat/(key+'.response')
    while not response.is_file():
        if (args.seat/'closed').is_file() or time.monotonic()>=deadline:
            raise TimeoutError('driver session ended')
        time.sleep(.2)
    print(response.read_text(encoding='utf-8'))


if __name__=='__main__':
    main()
