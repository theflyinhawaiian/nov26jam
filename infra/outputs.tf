output "public_ip" {
  description = "Point the Porkbun A record for the domain at this."
  value       = aws_eip.game.public_ip
}

output "ssh" {
  value = "ssh -i ~/.ssh/nov26jam ubuntu@${aws_eip.game.public_ip}"
}
