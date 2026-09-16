"""Mine rejected generated candidates through the runtime's existing deterministic checks."""
import json
import subprocess

import numpy as np
import torch


class ClaimMiner:
    def __init__(self, cli, initial, manifest):
        self.process = subprocess.Popen(["dotnet", str(cli), "validate-generated", str(initial)],
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8")
        self.allowed = torch.tensor(manifest["generatedOutputs"], device="cuda")
        self.to_input = torch.tensor(manifest["outputToInput"], device="cuda")
        self.bos = manifest["bosToken"]
        self.eos = manifest["eosToken"]
        self.rejected = 0

    def __call__(self, model, memory, memory_mask, rows):
        selected = [i for i, row in enumerate(rows) if row["projectResponse"]]
        if not selected:
            return None
        with torch.no_grad():
            source = memory[selected].detach()
            source_mask = memory_mask[selected]
            tokens = torch.full((len(selected), 1), self.bos, device="cuda", dtype=torch.long)
            finished = torch.zeros(len(selected), device="cuda", dtype=torch.bool)
            outputs = []
            for position in range(model.config["MaximumOutputTokens"]):
                logits = model.decode(tokens, source, source_mask)[:, -1]
                scores, choices = logits[:, self.allowed].float().topk(8)
                draw = torch.multinomial(scores.softmax(-1), 1)
                output = self.allowed[choices.gather(1, draw)[:, 0]]
                next_token = self.to_input[output]
                output = torch.where(finished, self.eos, output)
                next_token = torch.where(finished, self.eos, next_token)
                outputs.append(output)
                finished = finished | (next_token == self.eos)
                tokens = torch.cat([tokens, next_token[:, None]], 1)
                if position % 8 == 7 and finished.all().item():
                    break
            candidates = torch.stack(outputs, 1).cpu().tolist()
        candidates = [row[:row.index(self.eos)] if self.eos in row else row for row in candidates]
        self.process.stdin.write(json.dumps(dict(outputs=candidates, contexts=[rows[i]["claimContext"] for i in selected])) + "\n")
        self.process.stdin.flush()
        response = self.process.stdout.readline()
        if not response:
            raise RuntimeError("C# generated-claim validator stopped")
        rejected = json.loads(response)
        indices = [selected[i] for i, values in enumerate(rejected) if values]
        if not indices:
            return None
        sequences = [values for values in rejected if values]
        self.rejected += len(sequences)
        width = max(map(len, sequences))
        tokens = np.zeros((len(sequences), width), dtype=np.int64)
        mask = np.zeros_like(tokens, dtype=np.bool_)
        for i, values in enumerate(sequences):
            tokens[i, :len(values)] = values
            mask[i, :len(values)] = True
        return (torch.tensor(indices, device="cuda"), torch.as_tensor(tokens, device="cuda"),
                torch.as_tensor(mask, device="cuda"))

    def close(self):
        self.process.stdin.close()
        self.process.wait(timeout=30)
        self.process.stdout.close()
